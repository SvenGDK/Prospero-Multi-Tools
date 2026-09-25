// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Shell;
using SharpProspero.Application;
using SharpProspero.Interop;
using SharpProspero.Platform;
using System;

namespace ProsperoMultiTools.Data;

/// <summary>The outcome of a <see cref="BackupLauncher.Launch"/> call.</summary>
/// <param name="Started">True when the launch worker was armed and the app is about to exit; false
/// when a step ahead of the arm call failed and the caller stays alive to surface the reason.</param>
/// <param name="FailedStep">A short name identifying which step of the chain failed, or the empty
/// string on success.</param>
/// <param name="ErrorMessage">A single sentence describing what went wrong, or the empty string on
/// success.</param>
internal readonly record struct LaunchResult(bool Started, string FailedStep, string ErrorMessage)
{
    /// <summary>The result for a launch that reached the arm step and is about to exit.</summary>
    public static LaunchResult Ok() => new(true, string.Empty, string.Empty);

    /// <summary>The result for a launch that failed before the arm step.</summary>
    public static LaunchResult Fail(string step, string message) => new(false, step, message);
}

/// <summary>
/// The app-side half of the backup launch chain. Runs the five ordered pre-flight steps that make
/// a per-title nullfs overlay ready, arms the daemon's launch worker, then ends the caller's own
/// process so the shell's foreground slot is free when the worker fires
/// <c>sceSystemServiceLaunchApp</c>. The install pipeline in <see cref="BackupInstaller"/> must
/// have returned success before this call; a caller that skips the install would land on a
/// half-registered title id whose eboot the shell cannot resolve.
/// </summary>
internal static class BackupLauncher
{
    // The system-wide mount point every installed title binds under. The daemon's launch worker
    // unmounts the same path with a five-retry EBUSY loop after the launched process exits, so
    // repeat launches start on a clean mount table.
    private const string MountRoot = "/system_ex/app/";

    /// <summary>
    /// Runs the launch chain for <paramref name="titleId"/> against the backup folder at
    /// <paramref name="sourceFolder"/>. Returns a failing <see cref="LaunchResult"/> when a
    /// pre-arm step fails; on success the method arms the daemon's launch worker and calls
    /// <see cref="ProcessExit.Exit"/>, which does not return.
    /// </summary>
    /// <param name="titleId">The nine-character title id (for example <c>PPSA00000</c>) that names
    /// the installed backup.</param>
    /// <param name="sourceFolder">The backup folder the daemon binds at <c>/system_ex/app/&lt;TID&gt;</c>.
    /// This is the same folder <see cref="BackupInstaller"/> installed from.</param>
    /// <param name="progress">Receives one line per step so a screen host can show a moving
    /// status. May be null.</param>
    public static LaunchResult Launch(string titleId, string sourceFolder, IProgress<string>? progress = null)
    {
        if (string.IsNullOrEmpty(titleId))
            return LaunchResult.Fail("Validation", "The title id is empty.");
        if (string.IsNullOrEmpty(sourceFolder))
            return LaunchResult.Fail("Validation", "The source folder is empty.");

        // 1. Check whether a process already owns the title. A running foreground process holds
        // the shell's launch slot, so a second launch call from the daemon would return
        // SCE_SYSTEM_SERVICE_ERROR_LAUNCH_APP_INVALID before ever reaching the compositor. The
        // daemon walks the kernel process table, matches sceKernelGetAppInfo.title_id, and hands
        // the pid back through the reply's result word; -1 in that word means no match, which
        // is a valid answer here.
        progress?.Report($"Checking whether {titleId} is already running...");
        BrokerPid pidQuery = SandboxBroker.FindPidByTitleId(titleId);
        if (!pidQuery.Ok)
            return LaunchResult.Fail(
                "Find pid",
                $"The daemon could not read the process list ({DescribeOutcome(pidQuery.Outcome)}).");
        if (pidQuery.Running)
            return LaunchResult.Fail(
                "Find pid",
                $"The title {titleId} is already running as pid {pidQuery.Pid}. Close it first.");

        // 2. Confirm the per-title mount at /system_ex/app/<TID> is a nullfs pointing at the
        // source folder. The install worker leaves the mount in place on the successful path,
        // and the daemon's post-exit unmount only fires after a NOTE_EXIT event; so on a normal
        // second launch the mount is already there. Any other state - the mount got popped by a
        // reboot, an EBUSY retry gave up, the install step 8 unmounted a stale overlay and did
        // not remount, or the folder never held a mount at all - reads back as either statfs
        // failure or a non-nullfs filesystem, and this branch re-mounts before the arm call.
        string mountPoint = MountRoot + titleId;
        progress?.Report($"Confirming the mount at {mountPoint}...");
        BrokerStatfs statfs = SandboxBroker.Statfs(mountPoint);
        if (!statfs.IsNullfs)
        {
            progress?.Report($"Binding {sourceFolder} at {mountPoint}...");
            BrokerOutcome mountResult = SandboxBroker.MountNullfs(sourceFolder, mountPoint);
            if (mountResult != BrokerOutcome.Ok)
                return LaunchResult.Fail(
                    "Mount nullfs",
                    $"The daemon could not bind the source folder at {mountPoint} ({DescribeOutcome(mountResult)}).");
        }

        // 3. Read the foreground user id. sceSystemServiceLaunchApp needs a valid user id in its
        // context struct; a zero or invalid value makes the shell refuse the launch. The base
        // MultiToolsApp starts the user service at boot with priority 700, so the get-foreground
        // call answers here without a second initialize.
        progress?.Report("Reading the foreground user id...");
        int userId;
        try
        {
            userId = Users.ForegroundUserId;
        }
        catch (ProsperoException e)
        {
            return LaunchResult.Fail(
                "Read user id",
                $"The user service refused the foreground-user query ({e.Message}).");
        }
        if (userId <= 0 || userId == SceUser.Invalid)
            return LaunchResult.Fail(
                "Read user id",
                "No foreground user is signed in. Sign in a user before launching a backup.");

        // 4. Arm the daemon's launch worker. The daemon spawns a detached pthread that outlives
        // the caller: it sleeps two seconds so this process's own exit path completes, fires
        // sceSystemServiceLaunchApp with a { user_id, argv={NULL}, ctx={structsize,user_id,...} }
        // triple, polls FindPidByTitleId every 100 ms for up to five seconds to capture the
        // launched pid, blocks on kqueue/EVFILT_PROC/NOTE_EXIT for that pid, sleeps a three-
        // second grace window, then unmounts the nullfs overlay with a five-retry EBUSY loop.
        // The daemon acks the arm request as soon as the thread is spawned; a non-Ok outcome
        // here means the daemon could not spawn the worker (out of memory, thread limit, ...).
        progress?.Report($"Arming the launch worker for {titleId}...");
        BrokerOutcome armResult = SandboxBroker.ArmLaunchAndWaitForExit(titleId, (uint)userId, sourceFolder);
        if (armResult != BrokerOutcome.Ok)
            return LaunchResult.Fail(
                "Arm launch",
                $"The daemon could not arm the launch worker ({DescribeOutcome(armResult)}).");

        // 5. Exit so the shell's foreground slot frees before the worker's launch call fires.
        // ProcessExit.Exit calls sceSystemServiceLoadExec("exit", NULL) which the system-core
        // daemon acts on by killing this process from the outside on the same message loop
        // iteration the request lands on; the method itself does not return. The daemon's
        // launch worker is already sleeping its two-second pre-launch window and will fire
        // sceSystemServiceLaunchApp once that window closes.
        progress?.Report("Exiting so the launch worker can take over...");
        ProcessExit.Exit();

        // Unreached at runtime because ProcessExit.Exit spins forever on the return-to-shell
        // path. The return statement is here only so the method has a well-typed exit for the
        // compiler.
        return LaunchResult.Ok();
    }

    // Turns a broker outcome into a short human sentence a screen host can drop into a dialog.
    // The three non-Ok values map to distinct sentences so the reader can tell whether the
    // daemon was reachable, the daemon refused the request, or the wire came back truncated.
    private static string DescribeOutcome(BrokerOutcome outcome) => outcome switch
    {
        BrokerOutcome.Ok => "ok",
        BrokerOutcome.Unreachable => "the daemon is not reachable",
        BrokerOutcome.KernelError => "the daemon refused the request",
        BrokerOutcome.Malformed => "the daemon reply is malformed",
        _ => "unknown outcome",
    };
}
