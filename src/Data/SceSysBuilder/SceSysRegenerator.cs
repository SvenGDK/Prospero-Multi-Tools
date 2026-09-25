// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Shell;
using SharpProspero.Interop;
using SharpProspero.Storage;
using System;
using System.IO;

namespace ProsperoMultiTools.Data.SceSysBuilder;

/// <summary>
/// Outcome of a <see cref="SceSysRegenerator.PrepareStaging"/> call. Every regeneration is
/// best-effort: the counts let the caller surface a status line, but a missing input file (for
/// example a folder that never carried a <c>playgo-chunk.dat</c>) does not fail the pass.
/// </summary>
/// <param name="AssetsWritten">Number of image assets copied from the platform asset folder.</param>
/// <param name="KeystoneWritten">True when the keystone was regenerated.</param>
/// <param name="LicenseInfoWritten">True when license.info was regenerated for the new content id.</param>
/// <param name="LicenseDatWritten">True when license.dat was regenerated and re-signed.</param>
/// <param name="PlayGoChunkPatched">True when playgo-chunk.dat's content-id field was patched.</param>
/// <param name="PlayGoManifestWritten">True when the canonical playgo-manifest.xml was written.</param>
/// <param name="PsReservedWritten">True when the 8 KiB psreserved.dat was written.</param>
internal readonly record struct SceSysPrepareResult(
    int AssetsWritten,
    bool KeystoneWritten,
    bool LicenseInfoWritten,
    bool LicenseDatWritten,
    bool PlayGoChunkPatched,
    bool PlayGoManifestWritten,
    bool PsReservedWritten);

/// <summary>
/// Populates or refreshes every content-id-bound file inside a staging folder's <c>sce_sys</c>
/// subdirectory after <see cref="BackupSfoRewriter"/> has re-identified the SFO. The staging
/// folder is expected to already carry the emulator's base sce_sys (whatever the emulator ships
/// or the offline fake-pkg preparation added); this class only rewrites the files whose bytes
/// depend on the new title's content id, plus fills in any missing shell-visible image assets
/// or fixed template files.
/// </summary>
/// <remarks>
/// The regenerator is idempotent: a second call with the same content id produces the same
/// bytes. Every write routes through the sandbox broker when the staging folder lives on a
/// partition the module's mount namespace does not bind (<c>/data</c>), so the on-device
/// staging tree at <c>/data/homebrew/games/&lt;TID&gt;</c> is reachable from the app process.
/// </remarks>
internal static class SceSysRegenerator
{
    /// <summary>
    /// Passcode the shell's install path accepts for a debug-signed fake package. The 32-byte
    /// ASCII "0" string; every zero-passcode fake-pkg toolchain writes the same value into the
    /// gp4 project and the keystone's fingerprint hashes to the same bytes across every title.
    /// </summary>
    internal const string ZeroPasscode = "00000000000000000000000000000000";

    /// <summary>
    /// Regenerates every content-id-bound file and fills missing image assets. Returns a result
    /// record so the caller can surface a "regenerated N files" status line. Aborts on none of
    /// the sub-steps: a folder that never carried a playgo-chunk.dat gets no patch, a folder
    /// with no keystone still gets one written. The staging folder's <c>param.sfo</c> must have
    /// already been rewritten to the new content id before this method runs.
    /// </summary>
    /// <param name="stagingFolder">The per-title staging folder (e.g. <c>/data/homebrew/games/&lt;TID&gt;</c>).</param>
    /// <param name="newContentId">The content id the staging title now installs under.</param>
    /// <param name="platform">The emulator platform (PS1 / PS2 / PSP), used to select the asset folder.</param>
    internal static SceSysPrepareResult PrepareStaging(
        string stagingFolder,
        string newContentId,
        GamePlatform platform)
    {
        if (string.IsNullOrEmpty(stagingFolder))
            throw new ArgumentException("Staging folder is required.", nameof(stagingFolder));
        if (string.IsNullOrEmpty(newContentId))
            throw new ArgumentException("Content id is required.", nameof(newContentId));

        string sceSys = stagingFolder.TrimEnd('/') + "/sce_sys";
        EnsureDirectory(sceSys);

        // Refuse anything that is not PS2. The PS1 (ps1hd) and PSP (psphd) emulator folders
        // are fully-signed retail-shape fpkgs; every one of their sce_sys files is bound to
        // the folder's own per-title passcode, content id and signing keys and must never be
        // overwritten. Only PS2 emulators need the runtime regeneration because their sce_sys
        // was assembled offline from orbis-pub-cmd img_create against the emulator's own
        // content id and goes stale the moment the SFO is rewritten to a backup's content id.
        if (platform != GamePlatform.PS2)
            return new SceSysPrepareResult(0, false, false, false, false, false, false);

        int assets = AssetInjector.EnsureAssets(sceSys, platform);

        bool keystone = WriteFile(sceSys + "/keystone", KeystoneWriter.Create(ZeroPasscode));
        bool licenseInfo = WriteFile(sceSys + "/license.info", LicenseInfoWriter.Create(newContentId, SceContentType.Gd));
        bool licenseDat = WriteFile(sceSys + "/license.dat", LicenseDatWriter.Create(newContentId, SceContentType.Gd));

        // playgo-chunk.dat: patch the content-id field on the existing file when the emulator
        // shipped one; write nothing when it did not (the file is 416 bytes of chunk tables the
        // installer would not know how to synthesise from scratch on a per-emulator basis).
        bool playgoChunkPatched = false;
        byte[]? existingChunk = TryRead(sceSys + "/playgo-chunk.dat");
        if (existingChunk is not null)
        {
            byte[] patched = PlayGoChunkDatWriter.PatchContentId(existingChunk, newContentId);
            playgoChunkPatched = WriteFile(sceSys + "/playgo-chunk.dat", patched);
        }

        // playgo-manifest.xml: canonical single-chunk manifest is byte-identical across every
        // folder-install title, so a folder without a manifest gets the canonical one; a folder
        // that ships its own keeps it.
        bool playgoManifest = false;
        if (!FileExists(sceSys + "/playgo-manifest.xml"))
            playgoManifest = WriteFile(sceSys + "/playgo-manifest.xml", PlayGoManifestWriter.CreateDefault());

        // psreserved.dat: fixed 8 KiB zero buffer, only written when the folder does not ship one.
        bool psReserved = false;
        if (!FileExists(sceSys + "/psreserved.dat"))
            psReserved = WriteFile(sceSys + "/psreserved.dat", PsReservedWriter.Create());

        return new SceSysPrepareResult(
            assets,
            keystone,
            licenseInfo,
            licenseDat,
            playgoChunkPatched,
            playgoManifest,
            psReserved);
    }

    private static bool FileExists(string path)
    {
        if (FileSystem.Exists(path))
            return true;
        return SandboxBroker.IsOnBrokerPartition(path)
            && SandboxBroker.IsReachable()
            && SandboxBroker.FileExists(path);
    }

    private static byte[]? TryRead(string path)
    {
        try
        {
            if (FileSystem.Exists(path))
                return FileSystem.ReadAllBytes(path);
        }
        catch (ProsperoException) { }
        catch (IOException) { }

        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
        {
            var (outcome, bytes) = SandboxBroker.ReadAllBytes(path);
            if (outcome == BrokerOutcome.Ok && bytes.Length > 0)
                return bytes;
        }
        return null;
    }

    private static bool WriteFile(string path, byte[] data)
    {
        try
        {
            FileSystem.WriteAllBytes(path, data);
            return true;
        }
        catch (ProsperoException) { }
        catch (IOException) { }

        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            return SandboxBroker.WriteAllBytes(path, data) == BrokerOutcome.Ok;
        return false;
    }

    private static void EnsureDirectory(string path)
    {
        try
        {
            FileSystem.CreateDirectory(path);
        }
        catch (ProsperoException) { }
        catch (IOException) { }

        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            SandboxBroker.Mkdir(path, 0x1ED);
    }
}
