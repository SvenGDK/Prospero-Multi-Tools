// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Shell;
using SharpProspero.Interop;
using SharpProspero.Storage;
using SharpProspero.Storage.Sfo;
using System;

namespace ProsperoMultiTools.Data;

/// <summary>
/// Rewrites an emulator folder's <c>sce_sys/param.sfo</c> so the shell registers the currently
/// launching backup under the backup's own title and content ids instead of the emulator's own
/// package labels. The rewrite touches only <c>TITLE</c>, <c>TITLE_ID</c>, and <c>CONTENT_ID</c>
/// - the format bytes the shell reads to accept the module (<c>APP_TYPE</c>, <c>CATEGORY</c>,
/// <c>FORMAT</c>, <c>PARENTAL_LEVEL</c>, <c>SYSTEM_VER</c>, <c>APP_VER</c>, <c>VERSION</c>,
/// <c>DOWNLOAD_DATA_SIZE</c>, <c>ATTRIBUTE</c>) stay exactly as the emulator author shipped them.
/// The rewrite goes through the sandbox broker for paths on partitions the mount namespace does
/// not bind (an emulator staged under <c>/data/homebrew/emulators</c> lands on the broker route).
/// </summary>
internal static class BackupSfoRewriter
{
    /// <summary>
    /// Overwrites the emulator folder's SFO title, title id, and content id with the values
    /// derived from <paramref name="backup"/> so the installed shell row surfaces the backup's
    /// own name and content id. Returns <c>true</c> when the SFO was read, mutated, and written
    /// back; <c>false</c> when the emulator folder does not carry a readable SFO or when the
    /// backup does not supply a title id the shell can accept.
    /// </summary>
    /// <param name="emulatorFolder">The emulator folder the launch will install from.</param>
    /// <param name="backup">The backup whose title, title id and content id are copied in.</param>
    /// <param name="titleId">The 9-character title id the rewrite installs under (empty when
    /// the rewrite returned false).</param>
    public static bool ApplyBackupIdentity(string emulatorFolder, BackupInfo backup, out string titleId)
    {
        titleId = string.Empty;
        if (string.IsNullOrEmpty(emulatorFolder) || backup is null)
            return false;

        string sfoPath = emulatorFolder.TrimEnd('/') + "/sce_sys/param.sfo";
        byte[]? bytes = ReadAllBytes(sfoPath);
        if (bytes is null || bytes.Length == 0)
            return false;

        SfoFile? sfo = SfoFile.Read(bytes);
        if (sfo is null)
            return false;

        string np = NormaliseNpTitle(backup.GameId);
        if (np.Length != 9)
            return false;

        string title = SelectTitle(backup);
        string contentId = ComposeContentId(backup.Platform, np);

        // Mutate only the game-identity fields. Existing MaxLength values in the shipped SFO stay
        // in place when SetString is called without an explicit length; passing the exact spec
        // maxima below matches what a fresh SFO would carry when the shipped file's max is set
        // conservatively.
        sfo.SetString("TITLE", title, 128);
        sfo.SetString("TITLE_ID", np, 12);
        sfo.SetString("CONTENT_ID", contentId, 48);

        byte[] serialized = sfo.Write();
        if (!WriteAllBytes(sfoPath, serialized))
            return false;

        titleId = np;
        return true;
    }

    /// <summary>
    /// Returns the 9-character title id the SFO rewrite will use for <paramref name="backup"/>,
    /// or the empty string when the backup's own game id does not normalise to nine characters.
    /// A caller that needs to know the shell row's future title id without touching disk can
    /// use this helper to pre-fetch the value.
    /// </summary>
    public static string PredictTitleId(BackupInfo backup)
    {
        if (backup is null)
            return string.Empty;
        string np = NormaliseNpTitle(backup.GameId);
        return np.Length == 9 ? np : string.Empty;
    }

    /// <summary>
    /// Reduces a raw disc serial to the 9-character NP-title form the shell expects in the
    /// TITLE_ID slot: the letters and digits with every dash, underscore, dot, and whitespace
    /// character stripped. Returns the empty string when the input is null or empty; the caller
    /// must check the returned length before treating it as a valid TITLE_ID.
    /// </summary>
    public static string NormaliseNpTitle(string rawGameId)
    {
        if (string.IsNullOrEmpty(rawGameId))
            return string.Empty;

        var sb = new System.Text.StringBuilder(12);
        foreach (char c in rawGameId)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Composes the 36-byte content id the SFO rewrite writes for a given platform and 9-character
    /// title id. Exposed so callers that need to regenerate content-id-bound sce_sys files
    /// (license.info, license.dat, playgo-chunk.dat) after the SFO rewrite settle on the same
    /// string this class wrote.
    /// </summary>
    /// <param name="platform">Backup platform (PS1, PS2, PSP).</param>
    /// <param name="titleId">The 9-character NP title id (from <see cref="PredictTitleId"/>).</param>
    internal static string ComposeContentId(GamePlatform platform, string titleId) => platform switch
    {
        GamePlatform.PS1 => "UP9000-" + titleId + "_00-" + titleId + "PS1FPKG",
        GamePlatform.PS2 => "UP9000-" + titleId + "_00-" + titleId + "0000001",
        GamePlatform.PSP => "UP9000-" + titleId + "_00-" + titleId + "PSPFPKG",
        _ => "UP9000-" + titleId + "_00-" + titleId + "0000001",
    };

    private static string SelectTitle(BackupInfo backup)
    {
        string title = backup.Title ?? string.Empty;
        if (!string.IsNullOrEmpty(title))
            return title;
        string fallback = backup.DisplayTitle ?? string.Empty;
        return string.IsNullOrEmpty(fallback) ? backup.GameId : fallback;
    }

    private static byte[]? ReadAllBytes(string path)
    {
        try
        {
            if (FileSystem.Exists(path))
                return FileSystem.ReadAllBytes(path);
        }
        catch (ProsperoException) { }

        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
        {
            var (outcome, data) = SandboxBroker.ReadAllBytes(path);
            if (outcome == BrokerOutcome.Ok && data.Length > 0)
                return data;
        }
        return null;
    }

    private static bool WriteAllBytes(string path, byte[] data)
    {
        try
        {
            FileSystem.WriteAllBytes(path, data);
            return true;
        }
        catch (ProsperoException) { }
        catch (System.IO.IOException) { }

        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            return SandboxBroker.WriteAllBytes(path, data) == BrokerOutcome.Ok;
        return false;
    }
}
