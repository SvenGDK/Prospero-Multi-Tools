// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Shell;
using SharpProspero.Platform;
using SharpProspero.Storage;
using SharpProspero.Storage.Sfo;
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace ProsperoMultiTools.Data;

/// <summary>
/// The outcome of a backup install pass. On the success path <see cref="Success"/> is true and
/// <see cref="TitleId"/> carries the id the daemon registered the title under; on the failure
/// path <see cref="FailedStep"/> names the ordered step that stopped the chain and
/// <see cref="ErrorMessage"/> reads out the specific reason so a caller can render it in the UI
/// without a second lookup.
/// </summary>
/// <param name="Success">True when every step returned a zero result and the shell holds the title.</param>
/// <param name="TitleId">The 9-character title id the chain installed, resolved from the source folder.</param>
/// <param name="FailedStep">The 1-based step number the chain stopped at, or zero on success.</param>
/// <param name="StepName">The short human-readable name of the failing step, or an empty string on success.</param>
/// <param name="ErrorMessage">A one-line explanation of the failure, or an empty string on success.</param>
/// <param name="RawKlog">
/// The klog line the daemon emitted for the failing step, when known. Empty when the failure
/// itself was on the app side (source folder missing, titleId not resolvable, escalation refused).
/// </param>
internal readonly record struct InstallResult(
    bool Success,
    string TitleId,
    int FailedStep,
    string StepName,
    string ErrorMessage,
    string RawKlog);

/// <summary>
/// The install pass a launcher runs before the first launch of a folder-backup title the shell
/// has no record of. Runs the full 23-step chain through the sandbox broker (mounts,
/// AppInstUtil calls, recursive copies, appmeta layout writes, trophy binding drops), so on
/// return either the shell holds a fully-registered row for the title id or the caller sees a
/// specific step and its klog line.
/// </summary>
internal static class BackupInstaller
{
    private const string SystemExAppPrefix = "/system_ex/app/";
    private const string UserAppPrefix = "/user/app/";
    private const string UserAppRoot = "/user/app/";

    /// <summary>
    /// Runs the ordered install chain for <paramref name="sourceFolder"/> and returns the
    /// outcome. When <paramref name="titleId"/> is empty the id is resolved from
    /// <c>sce_sys/param.json</c> (or <c>param.sfo</c> as a fallback); a resolution failure is
    /// reported through <see cref="InstallResult.FailedStep"/>. Progress reports at every step
    /// let the calling screen show <c>step X of 23: name</c> while the chain runs.
    /// </summary>
    public static InstallResult Install(
        string sourceFolder,
        string titleId,
        IProgress<string>? progress = null,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(sourceFolder))
            return Fail(0, "Preflight", "The source folder is empty.");

        string source = sourceFolder.TrimEnd('/');
        string srcSceSys = source + "/sce_sys";

        // ---- Step 1: preflight the source folder ----
        Report(progress, 1, 23, "Preflight source folder");
        cancel.ThrowIfCancellationRequested();
        if (!PathExists(source))
            return Fail(1, "Preflight source folder", "Source folder does not exist: " + source);

        // ---- Step 2: verify sce_sys is present and a directory ----
        Report(progress, 2, 23, "Verify sce_sys");
        cancel.ThrowIfCancellationRequested();
        if (!PathIsDirectory(srcSceSys))
            return Fail(2, "Verify sce_sys", "sce_sys folder not found under: " + source);

        // ---- Step 3: escalate to widen the daemon's file view ----
        Report(progress, 3, 23, "Escalate");
        cancel.ThrowIfCancellationRequested();
        UnjailResult unjail = UnjailRequest.Request();
        if (!unjail.Applied)
            return Fail(3, "Escalate", "The escalation daemon refused: " + unjail.Reason);

        // ---- Step 4: resolve the title id ----
        Report(progress, 4, 23, "Read title id");
        cancel.ThrowIfCancellationRequested();
        string resolvedId = string.IsNullOrEmpty(titleId)
            ? ResolveTitleId(srcSceSys) ?? string.Empty
            : titleId;
        if (string.IsNullOrEmpty(resolvedId))
            return Fail(4, "Read title id",
                "Could not read title id from sce_sys/param.json or sce_sys/param.sfo.");

        // ---- Step 5: on-screen notification banner ----
        Report(progress, 5, 23, "Notify start");
        cancel.ThrowIfCancellationRequested();
        TryNotify("Installing " + resolvedId + "...");

        // ---- Step 6: build /data/imgmnt working tree ----
        Report(progress, 6, 23, "Prepare /data/imgmnt tree");
        cancel.ThrowIfCancellationRequested();
        InstallResult imgmnt = MakeImgmntTree();
        if (imgmnt.FailedStep != 0)
            return imgmnt with { FailedStep = 6, StepName = "Prepare /data/imgmnt tree", TitleId = resolvedId };

        // ---- Step 7: create /system_ex/app/<TID> ----
        Report(progress, 7, 23, "Create /system_ex/app mount point");
        cancel.ThrowIfCancellationRequested();
        string mountPoint = SystemExAppPrefix + resolvedId;
        BrokerOutcome mkOutcome = SandboxBroker.Mkdir(mountPoint, 0x1ED);
        if (mkOutcome != BrokerOutcome.Ok)
        {
            // Some shells hand back EEXIST here; probe with Stat and treat an existing directory
            // as success, treat everything else as a hard fail.
            BrokerStat probe = SandboxBroker.Stat(mountPoint);
            if (!probe.IsDirectory)
                return Fail(7, "Create /system_ex/app mount point",
                    "mkdir " + mountPoint + " failed: " + mkOutcome, resolvedId);
        }

        // ---- Step 8: unmount a leftover nullfs at the destination ----
        Report(progress, 8, 23, "Clear leftover nullfs mount");
        cancel.ThrowIfCancellationRequested();
        BrokerStatfs beforeMount = SandboxBroker.Statfs(mountPoint);
        bool mountAlreadyBound = false;
        if (beforeMount.IsNullfs)
        {
            BrokerOutcome unmountRc = SandboxBroker.Unmount(mountPoint, 0);
            if (unmountRc == BrokerOutcome.Ok)
            {
                // Cleanly popped; step 9 will re-bind fresh.
            }
            else
            {
                // The daemon's post-exit 5-retry unmount already tried the same syscall; if it
                // failed there and still fails here, the shell holds a ref the caller cannot
                // force-release without MNT_FORCE (which risks corruption). The staging path is
                // stable per TID (see EmulatorLaunchScreen.StagingFolderRoot + resolvedId), so
                // the existing nullfs must already point at the same source folder. Trust it
                // and skip step 9's re-mount so the launch chain can proceed.
                mountAlreadyBound = true;
            }
        }

        // ---- Step 9: nullfs-bind the source folder over the mount point ----
        Report(progress, 9, 23, "Bind mount source over /system_ex/app/" + resolvedId);
        cancel.ThrowIfCancellationRequested();
        if (!mountAlreadyBound)
        {
            BrokerOutcome mount = SandboxBroker.MountNullfs(source, mountPoint);
            if (mount != BrokerOutcome.Ok)
                return Fail(9, "Bind mount source over /system_ex/app",
                    "nullfs mount " + source + " -> " + mountPoint + " failed: " + mount,
                    resolvedId);
        }

        // ---- Step 10: mid-install "Fixing Config" notification ----
        Report(progress, 10, 23, "Notify config fixup");
        cancel.ThrowIfCancellationRequested();
        TryNotify("Fixing Config, please wait...");

        // ---- Step 11: initialise the daemon's copy of libSceAppInstUtil ----
        Report(progress, 11, 23, "AppInstUtil initialize");
        cancel.ThrowIfCancellationRequested();
        BrokerOutcome initRc = SandboxBroker.AppInstUtilInitialize();
        if (initRc != BrokerOutcome.Ok)
            return Fail(11, "AppInstUtil initialize",
                "sceAppInstUtilInitialize failed: " + initRc, resolvedId);

        // ---- Step 12: intentionally skipped ----
        // sceAppInstUtilAppUnInstall is NEVER called by this pipeline. Uninstalling a shell
        // row is a manual user action that only the user performs from the console UI. Any
        // reinstall of an id we already own is handled by step 20's register call updating
        // the same row in place; the previous folder contents (if any) get overwritten by
        // step 17's recursive sce_sys copy. No broker uninstall request leaves this app.
        Report(progress, 12, 23, "Skip uninstall (manual only)");
        cancel.ThrowIfCancellationRequested();

        // ---- Step 13: copy trophy bindings into /system_data/priv/appmeta/<TID> ----
        Report(progress, 13, 23, "Copy trophy bindings");
        cancel.ThrowIfCancellationRequested();
        BrokerOutcome trophy = SandboxBroker.UpdateTrophy(resolvedId, srcSceSys);
        if (trophy != BrokerOutcome.Ok)
            return Fail(13, "Copy trophy bindings",
                "UpdateTrophy failed: " + trophy, resolvedId);

        // ---- Step 14: snd0info UPDATE is intentionally skipped ----
        // The snd0.at9 preview audio file still lands via the *.at9 allowlist in step 18; only
        // the SQLite row update on /system_data/priv/mms/app.db is skipped here because no
        // callable SQLite entry point reaches an app-module process on this firmware. The
        // shell rescans the row the next time it walks the app metadata.
        Report(progress, 14, 23, "Skip snd0info UPDATE (rescanned by shell)");
        cancel.ThrowIfCancellationRequested();

        // ---- Step 15: remount /system_ex so the new subdir enters the shell's cached view ----
        Report(progress, 15, 23, "Remount /system_ex");
        cancel.ThrowIfCancellationRequested();
        BrokerOutcome remount = SandboxBroker.RemountSystemEx();
        if (remount != BrokerOutcome.Ok)
            return Fail(15, "Remount /system_ex",
                "MNT_UPDATE remount of /system_ex failed: " + remount, resolvedId);

        // ---- Step 16: build /user/app/<TID> and /user/app/<TID>/sce_sys ----
        Report(progress, 16, 23, "Create /user/app tree");
        cancel.ThrowIfCancellationRequested();
        string userAppTitle = UserAppPrefix + resolvedId;
        string userAppSceSys = userAppTitle + "/sce_sys";
        BrokerOutcome mkTitle = SandboxBroker.Mkdir(userAppTitle, 0x1ED);
        if (mkTitle != BrokerOutcome.Ok && !SandboxBroker.Stat(userAppTitle).IsDirectory)
            return Fail(16, "Create /user/app tree",
                "mkdir " + userAppTitle + " failed: " + mkTitle, resolvedId);
        BrokerOutcome mkSceSys = SandboxBroker.Mkdir(userAppSceSys, 0x1ED);
        if (mkSceSys != BrokerOutcome.Ok && !SandboxBroker.Stat(userAppSceSys).IsDirectory)
            return Fail(16, "Create /user/app tree",
                "mkdir " + userAppSceSys + " failed: " + mkSceSys, resolvedId);

        // ---- Step 17: recursive copy of sce_sys into /user/app/<TID>/sce_sys ----
        Report(progress, 17, 23, "Copy sce_sys into /user/app/" + resolvedId + "/sce_sys");
        cancel.ThrowIfCancellationRequested();
        BrokerCopyResult copyResult =
            SandboxBroker.CopyDirRecursive(srcSceSys, userAppSceSys);
        if (!copyResult.Ok)
            return Fail(17, "Copy sce_sys into /user/app tree",
                "Recursive copy " + srcSceSys + " -> " + userAppSceSys +
                " failed: " + copyResult.Outcome + " (files=" + copyResult.FilesCopied + ")",
                resolvedId);

        // ---- Step 18: copy appmeta-classed files into /user/appmeta/<TID> ----
        Report(progress, 18, 23, "Copy appmeta files");
        cancel.ThrowIfCancellationRequested();
        BrokerCopyResult appmetaResult =
            SandboxBroker.CopySceSysToAppmeta(srcSceSys, resolvedId);
        if (!appmetaResult.Ok)
            return Fail(18, "Copy appmeta files",
                "CopySceSysToAppmeta failed: " + appmetaResult.Outcome +
                " (files=" + appmetaResult.FilesCopied + ")", resolvedId);

        // ---- Step 19: mid-install "Installing <TID>" notification ----
        Report(progress, 19, 23, "Notify install register");
        cancel.ThrowIfCancellationRequested();
        TryNotify("Installing " + resolvedId + ", please wait...");

        // ---- Step 20: register the title with the shell's app database ----
        Report(progress, 20, 23, "AppInstUtil register title dir");
        cancel.ThrowIfCancellationRequested();
        BrokerOutcome register = SandboxBroker.AppInstUtilAppInstallTitleDir(resolvedId, UserAppRoot);
        if (register != BrokerOutcome.Ok)
            return Fail(20, "AppInstUtil register title dir",
                "sceAppInstUtilAppInstallTitleDir failed: " + register, resolvedId);

        // ---- Step 21: diagnostic mount.lnk breadcrumb ----
        Report(progress, 21, 23, "Write mount.lnk breadcrumb");
        cancel.ThrowIfCancellationRequested();
        string mountLnk = userAppTitle + "/mount.lnk";
        byte[] mountLnkBytes = Encoding.UTF8.GetBytes(source);
        BrokerOutcome mountLnkOutcome = SandboxBroker.WriteAllBytes(mountLnk, mountLnkBytes);
        if (mountLnkOutcome != BrokerOutcome.Ok)
            return Fail(21, "Write mount.lnk breadcrumb",
                "Write " + mountLnk + " failed: " + mountLnkOutcome, resolvedId);

        // ---- Step 22: folder-mode invariant - do NOT delete source/sce_sys ----
        // The install pass runs in folder-image mode; there is no cleanup branch that removes
        // the source sce_sys folder. This step exists as a barrier so a future edit that adds
        // a cleanup on this path fails the audit at review time.
        Report(progress, 22, 23, "Folder-mode invariant (no source cleanup)");
        cancel.ThrowIfCancellationRequested();

        // ---- Step 23: install-completed notification ----
        Report(progress, 23, 23, "Notify installed and ready");
        cancel.ThrowIfCancellationRequested();
        TryNotify(resolvedId + " installed and ready to use!");

        return new InstallResult(true, resolvedId, 0, string.Empty, string.Empty, string.Empty);
    }

    // ---- Step 6 helper: the fixed /data/imgmnt tree ----

    private static InstallResult MakeImgmntTree()
    {
        ReadOnlySpan<string> tree =
        [
            "/data/imgmnt",
            "/data/imgmnt/exfatmnt",
            "/data/imgmnt/pfsmnt",
            "/data/imgmnt/pfscmnt",
            "/data/imgmnt/ufsmnt",
        ];
        foreach (string dir in tree)
        {
            BrokerOutcome mk = SandboxBroker.Mkdir(dir, 0x1ED);
            if (mk == BrokerOutcome.Ok)
                continue;
            // EEXIST is expected on the second run of the chain; probe with Stat and treat an
            // existing directory as success.
            if (SandboxBroker.Stat(dir).IsDirectory)
                continue;
            return Fail(6, "Prepare /data/imgmnt tree",
                "mkdir " + dir + " failed: " + mk);
        }
        return new InstallResult(true, string.Empty, 0, string.Empty, string.Empty, string.Empty);
    }

    // ---- Preflight path probes ----

    private static bool PathExists(string path)
    {
        if (FileSystem.Exists(path))
            return true;
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            return SandboxBroker.Stat(path).Ok;
        return false;
    }

    private static bool PathIsDirectory(string path)
    {
        // Direct probe first - a bound partition answers without a broker round trip.
        if (FileSystem.IsDirectory(path))
            return true;
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            return SandboxBroker.Stat(path).IsDirectory;
        return false;
    }

    // ---- Title-id resolution ----

    private static string? ResolveTitleId(string sceSysDir)
    {
        string paramJson = sceSysDir + "/param.json";
        string? fromJson = TryReadTitleIdFromParamJson(paramJson);
        if (!string.IsNullOrEmpty(fromJson))
            return fromJson;
        string paramSfo = sceSysDir + "/param.sfo";
        return TryReadTitleIdFromParamSfo(paramSfo);
    }

    private static string? TryReadTitleIdFromParamJson(string path)
    {
        byte[]? bytes = TryReadAllBytes(path);
        if (bytes is null || bytes.Length == 0)
            return null;
        string text = Encoding.UTF8.GetString(bytes);
        if (!JsonValue.TryParse(text, out JsonValue root))
            return null;
        if (root.IsNull || root.Type != JsonType.Object)
            return null;
        if (root.TryGet("titleId", out JsonValue v1) && v1.Type == JsonType.String)
        {
            string s = v1.AsString(string.Empty);
            if (!string.IsNullOrEmpty(s))
                return s;
        }
        if (root.TryGet("title_id", out JsonValue v2) && v2.Type == JsonType.String)
        {
            string s = v2.AsString(string.Empty);
            if (!string.IsNullOrEmpty(s))
                return s;
        }
        return null;
    }

    private static string? TryReadTitleIdFromParamSfo(string path)
    {
        byte[]? bytes = TryReadAllBytes(path);
        if (bytes is null || bytes.Length < 20)
            return null;
        SfoFile? sfo = SfoFile.Read(bytes);
        if (sfo is null)
            return null;
        string? titleId = sfo.GetString("TITLE_ID");
        return string.IsNullOrEmpty(titleId) ? null : titleId;
    }

    private static byte[]? TryReadAllBytes(string path)
    {
        // Broker route for /data and /user; the caller may have staged the backup folder either
        // way, so both routes are tried before giving up.
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
        {
            var (outcome, bytes) = SandboxBroker.ReadAllBytes(path);
            if (outcome == BrokerOutcome.Ok && bytes.Length > 0)
                return bytes;
        }
        try
        {
            if (FileSystem.Exists(path))
                return FileSystem.ReadAllBytes(path);
        }
        catch (IOException) { }
        catch (SharpProspero.Interop.ProsperoException) { }
        return null;
    }

    // ---- Notifications and progress reports ----

    private static void TryNotify(string message)
    {
        try
        {
            Notification.Show(message);
        }
        catch (Exception)
        {
            // A notification failure never fails the install pass; the on-screen toast is a
            // convenience for the user, not a barrier.
        }
    }

    private static void Report(IProgress<string>? progress, int step, int total, string what)
    {
        if (progress is null)
            return;
        progress.Report("step " + step + " of " + total + ": " + what);
    }

    private static InstallResult Fail(int step, string stepName, string message,
        string titleId = "", string rawKlog = "")
    {
        return new InstallResult(false, titleId, step, stepName, message, rawKlog);
    }
}
