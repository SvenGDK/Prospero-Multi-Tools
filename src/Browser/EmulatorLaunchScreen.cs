// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Data;
using ProsperoMultiTools.Data.SceSysBuilder;
using ProsperoMultiTools.Emulator;
using ProsperoMultiTools.Shell;
using SharpProspero.Application;
using SharpProspero.Interop;
using SharpProspero.Storage;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProsperoMultiTools.Browser;

/// <summary>
/// Presents per-game emulator options for a backup and drives the two-step install-then-launch
/// flow. The whole deploy (config emit, disc copy, install pass, launch arm) runs on a background
/// thread with a status label that stays responsive to joystick navigation and a Cancel button
/// bound to the same cancellation token as the copy loop, so a four-gigabyte PS2 ISO does not
/// block the shell and a mid-install cancel unwinds cleanly. The install pass registers the
/// emulator folder with the shell's app database through the sandbox broker's 23-step chain, and
/// the launch call arms the daemon's launch worker and asks the system to end this process so the
/// worker's <c>sceSystemServiceLaunchApp</c> reaches the compositor with a free foreground slot.
/// </summary>
internal sealed class EmulatorLaunchScreen : MultiToolsScreen
{
    private readonly BackupInfo _backup;
    private readonly IEmuOptions _options;

    // The emulator the launch is bound to. Mutable so a PS2 backup with more than one resolved
    // emulator descriptor can be re-routed from the on-screen selector before Launch is pressed.
    private EmulatorDescriptor _emu;

    // PS2 selector state: every descriptor of kind PS2 whose folder resolves on device, resolved
    // once at construction, and the index of the one currently bound to _emu. When only one
    // descriptor resolves the selector row is suppressed and this list still carries that one so
    // the getter that decides whether to draw the row can answer without re-probing.
    private readonly List<EmulatorDescriptor> _resolvedPs2Emulators;

    private Task? _pendingLaunch;
    private CancellationTokenSource? _launchCts;
    private volatile string _launchStatus = "";
    private volatile string _launchedFolder = "";
    private long _launchDone;
    private long _launchTotal;
    private long _launchLastRedraw;
    private bool _launching;

    public EmulatorLaunchScreen(MultiToolsShell shell, BackupInfo backup,
        EmulatorDescriptor emu, IEmuOptions options) : base(shell)
    {
        _backup = backup;
        _emu = emu;
        _options = options;

        // Pre-fill the emulator's title id from the backup so the config points at the right game
        // even when the user has not opened one of the platform's dedicated per-game screens.
        if (_options is Ps2EmuOptions ps2 && !string.IsNullOrEmpty(backup.GameId))
            ps2.Ps2TitleId = FormatPs2TitleId(backup.GameId);
        if (_options is Ps1EmuOptions ps1 && !string.IsNullOrEmpty(backup.GameId))
            ps1.Ps1TitleId = backup.GameId;
        if (_options is PspEmuOptions psp && !string.IsNullOrEmpty(backup.GameId))
            psp.PspTitleId = backup.GameId;

        // A PS2 backup may have more than one installed emulator on device (Jak, Rogue, ...). The
        // launch screen exposes a selector row for that case so the user can pick before pressing
        // Launch. ResolveAllForKind returns only descriptors whose folder currently resolves, so
        // the selector's rows always name an emulator whose runtime is actually installed.
        if (_options is Ps2EmuOptions)
        {
            _resolvedPs2Emulators = new List<EmulatorDescriptor>(
                EmulatorRegistry.ResolveAllForKind(GamePlatform.PS2));
            // Guarantee the descriptor the caller passed in is one of the selector's rows so a
            // manifest entry that does not resolve on the register's own probe still lets the
            // user launch it. ResolveAllForKind uses the manifest order, so a match by name keeps
            // that order intact and the currently bound emulator stays visible in the selector.
            bool alreadyListed = false;
            foreach (EmulatorDescriptor d in _resolvedPs2Emulators)
            {
                if (string.Equals(d.Name, _emu.Name, StringComparison.Ordinal))
                {
                    alreadyListed = true;
                    break;
                }
            }
            if (!alreadyListed)
                _resolvedPs2Emulators.Insert(0, _emu);
        }
        else
        {
            _resolvedPs2Emulators = new List<EmulatorDescriptor>();
        }
    }

    public override string Title => "Launch " + _backup.DisplayTitle;

    public override string Hint => _launching
        ? "Preparing the emulator - please wait."
        : "Adjust options, then select Launch.";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 600 };

        menu.Add(new KeyValueRow("Title", _backup.DisplayTitle));
        if (!string.IsNullOrEmpty(_backup.GameId))
            menu.Add(new KeyValueRow("Game ID", _backup.GameId));
        if (!string.IsNullOrEmpty(_backup.Region))
            menu.Add(new KeyValueRow("Region", _backup.Region));

        // PS2 selector: only when more than one emulator descriptor of kind PS2 currently
        // resolves on device. The single-descriptor case surfaces the bound emulator's name as
        // an unchangeable label row, which is the same shape the other platforms use.
        if (_options is Ps2EmuOptions && _resolvedPs2Emulators.Count > 1)
        {
            var labels = new string[_resolvedPs2Emulators.Count];
            int selected = 0;
            for (int i = 0; i < _resolvedPs2Emulators.Count; i++)
            {
                labels[i] = _resolvedPs2Emulators[i].Name;
                if (string.Equals(_resolvedPs2Emulators[i].Name, _emu.Name, StringComparison.Ordinal))
                    selected = i;
            }
            menu.Add(new OptionSelector("PS2 emulator", labels, selected, i =>
            {
                if (i >= 0 && i < _resolvedPs2Emulators.Count)
                {
                    _emu = _resolvedPs2Emulators[i];
                    RebuildScreen();
                }
            }));
        }
        else
        {
            menu.Add(new KeyValueRow("Emulator", _emu.Name));
        }

        menu.Add(new Separator());

        // Let the options model build its own rows.
        _options.BuildOptionRows(menu, Shell, RebuildScreen);

        menu.Add(new Separator());

        if (_launching)
        {
            string status = _launchStatus;
            long totalNow = Interlocked.Read(ref _launchTotal);
            long doneNow = Interlocked.Read(ref _launchDone);
            if (totalNow > 0)
                status += $" ({FormatMB(doneNow)} / {FormatMB(totalNow)})";
            menu.Add(new Label(status.Length == 0 ? "Working..." : status) { TextColor = Shell.Theme.TextMuted });
            menu.Add(new Button("Cancel", CancelLaunch));
        }
        else
        {
            menu.Add(new Button("Launch", StartLaunch));
        }

        return menu;
    }

    public override void Tick(FrameContext context)
    {
        if (!_launching || _pendingLaunch is null)
            return;

        if (!_pendingLaunch.IsCompleted)
        {
            // Redraw the status line when progress has advanced by at least one megabyte so the
            // counter moves without firing a rebuild on every frame. A rebuild also fires on
            // second-frame boundaries so a stalled copy still shows a heartbeat and cancellation
            // stays responsive.
            long doneBytes = Interlocked.Read(ref _launchDone);
            long lastRedraw = Interlocked.Read(ref _launchLastRedraw);
            const long OneMB = 1024L * 1024L;
            if (doneBytes - lastRedraw >= OneMB || (context.FrameIndex & 63) == 0)
            {
                Interlocked.Exchange(ref _launchLastRedraw, doneBytes);
                RebuildScreen();
            }
            return;
        }

        Task finished = _pendingLaunch;
        _pendingLaunch = null;
        _launching = false;

        if (finished.IsCanceled)
        {
            Shell.Notify("Launch canceled.");
        }
        else if (finished.IsFaulted)
        {
            Exception e = finished.Exception?.InnerException ?? finished.Exception!;
            Shell.ReportFailure("Launch", e, true);
        }
        else
        {
            // The Deploy task runs the disc/config emit AND the install-then-launch chain. On the
            // successful path BackupLauncher.Launch arms the daemon's launch worker and calls
            // ProcessExit.Exit, which spins forever until the system-core daemon reaps this
            // process; the Task never reaches this "RanToCompletion" branch. If it does, the
            // launch chain returned normally without exiting - a broken contract in the SDK, or
            // a future refactor that changed the spin's behaviour - and the shell surfaces the
            // condition rather than falling through silently to a blank screen.
            Shell.ReportFailure("Launch",
                new Exception("The launch chain returned normally without ending this process."),
                true);
        }

        RebuildScreen();
    }

    protected override void OnDispose()
    {
        // Cancel any deploy still running. The CTS is not disposed here because the background
        // Task is still holding a reference to its token and will read it once more before it
        // exits; disposing the source under the running check is what raced with the copy loop
        // on the previous attempt at this cleanup. GC picks up the CTS with the Task once both
        // have finished.
        try { _launchCts?.Cancel(); } catch { }
        _launchCts = null;
        _pendingLaunch = null;
    }

    private void CancelLaunch()
    {
        _launchCts?.Cancel();
    }

    private void StartLaunch()
    {
        if (_launching)
            return;

        // Flip _launching first so a fast second Launch press during the synchronous unjail
        // request or folder probe below cannot re-enter and start a second deploy Task alongside
        // this one. The rest of the setup below either succeeds (Task starts) or leaves this
        // flag on with an error dialog shown; the user's next action is to close the dialog and
        // press Launch again, at which point the flag is still on and we simply return.
        _launching = true;

        try
        {
            // (a) Ensure the process is unjailed for filesystem and mount access. The daemon may
            //     not be running, in which case /data / /user / /system_ex all answer as
            //     unreachable and the bind mount that follows would fail anyway; surface the
            //     daemon's own reason plainly.
            UnjailResult unjail = UnjailRequest.Request();
            if (!unjail.Applied)
            {
                Shell.ReportFailure("Unjail", new Exception(unjail.Reason), true);
                _launching = false;
                return;
            }
            Places.Configure(unjail);

            // (b) Resolve the emulator folder from the first existing probe path.
            string? sourceFolder = EmulatorRegistry.ResolveFolder(_emu);
            if (sourceFolder is null)
            {
                // Show every path the shell looked at, mark the ones that did not resolve, and
                // spell out the layout the emulator folder needs so a user without a document
                // to hand can act on the dialog alone.
                var reasons = new System.Text.StringBuilder();
                reasons.Append("The emulator folder '").Append(_emu.Name)
                    .Append("' was not found at any of these paths:\n");
                foreach (string probe in _emu.SourceFolderProbe)
                {
                    reasons.Append("  ").Append(probe);
                    if (!FileSystem.Exists(probe)
                        && !(SandboxBroker.IsOnBrokerPartition(probe)
                             && SandboxBroker.IsReachable()
                             && SandboxBroker.IsDirectory(probe)))
                        reasons.Append("  -  not present");
                    reasons.Append('\n');
                }
                string preferred = "/data/homebrew/emulators/" + _emu.Name;
                reasons.Append('\n')
                    .Append("Copy the emulator runtime to:\n")
                    .Append("  ").Append(preferred).Append("\n\n")
                    .Append("USB alternatives: /mnt/usb0../mnt/usb7/homebrew/").Append(_emu.Name).Append('\n')
                    .Append('\n')
                    .Append("The emulator folder must contain:\n")
                    .Append("  eboot.bin              (the PS4 emulator binary)\n")
                    .Append("  sce_module/            (its shipped modules, e.g. libc.prx)\n")
                    .Append("  sce_sys/param.sfo      (title id, title name, icon size)\n")
                    .Append("  sce_sys/icon0.png      (512x512, 24-bit)\n")
                    .Append("  sce_sys/pic0.png       (1920x1080, 24-bit)\n");
                switch (_emu.Kind)
                {
                    case EmulatorKind.PS1:
                        reasons.Append("  data/                  (backup discs land here as disc1.bin, disc2.bin, ...)\n");
                        break;
                    case EmulatorKind.PS2:
                        reasons.Append("  image/                 (backup discs land here as disc01.iso, disc02.iso, ...)\n");
                        reasons.Append("  patches/               (per-game LUA / TXT / PS3 configs)\n");
                        reasons.Append("  lua_include/           (shared script library)\n");
                        break;
                    case EmulatorKind.PSP:
                        reasons.Append("  data/USER_L0.IMG       (backup UMD image, copied here on launch)\n");
                        break;
                }
                reasons.Append('\n')
                    .Append("The backup itself does not need to be inside the emulator folder: the\n")
                    .Append("launcher copies the disc image into the folder above before the emulator\n")
                    .Append("is started, so a backup on any USB or under /data is accepted.");

                Shell.ReportFailure("Launch", new Exception(reasons.ToString()), true);
                _launching = false;
                return;
            }

            _launchCts = new CancellationTokenSource();
            _launchStatus = "Preparing...";
            Interlocked.Exchange(ref _launchDone, 0);
            Interlocked.Exchange(ref _launchTotal, 0);

            CancellationToken token = _launchCts.Token;
            string folder = sourceFolder;

            _pendingLaunch = Task.Run(() => Deploy(folder, token), token);
        }
        catch
        {
            _launching = false;
            throw;
        }
        RebuildScreen();
    }

    // Runs on a background thread. Every branch is idempotent so a canceled or re-run launch
    // leaves no half-written config or half-deployed disc image behind. The method has three
    // phases: (a) write the emulator's config file and copy the disc images into the emulator
    // folder, (b) run BackupInstaller.Install to register the emulator folder with the shell's
    // app database through the 23-step broker chain, and (c) call BackupLauncher.Launch to arm
    // the daemon's launch worker and end this process so the worker's launch call reaches the
    // compositor with a free foreground slot. Phase (a) preserves the pre-install disc/config
    // deployment; phase (b) is the sandbox-broker-served install pass; phase (c) is the launch
    // via the daemon-owned worker. Any failure in (b) or (c) throws an exception that propagates
    // through the Task and reaches Tick's IsFaulted branch, which shows the reason in a full
    // system dialog.
    private void Deploy(string sourceFolder, CancellationToken token)
    {
        // ---- Phase (a): stage ----
        // Every launch runs against a per-title staging folder under /data/homebrew/games/<TID>
        // that carries a full copy of the emulator plus this backup's own discs and config
        // overrides. The emulator source folder (sourceFolder) stays untouched from this point
        // on - no config emit, no SFO mutation, no disc copy, no install-time mount is aimed at
        // it. That guarantees the emulator survives an install and a later uninstall unchanged,
        // and lets a second launch of a different backup drop into its own staging folder
        // without stepping on the first.
        string targetTitleId = ResolveTargetTitleId(sourceFolder);
        if (string.IsNullOrEmpty(targetTitleId))
            throw new InvalidOperationException(
                "Neither the backup metadata nor the emulator's sce_sys/param.sfo carries a "
                + "title id the shell can register the module under.");

        string stagingFolder = StagingFolderRoot + "/" + targetTitleId;
        SetStatus("Staging " + targetTitleId + " at " + stagingFolder + " ...");
        EnsureDirectory(StagingFolderRoot);
        EnsureDirectory(stagingFolder);
        SetStatus("Copying emulator into staging ...");
        // The daemon-side recursive copy uses one broker round trip per subtree instead of one
        // per 2 KiB chunk, so a multi-hundred-MB emulator (assets/, data/, ...) completes
        // without exhausting ephemeral TCP ports and without leaving out the deeper folders the
        // app-side walker occasionally missed on stale broker listings. The fallback path stays
        // available for cases the daemon rejects the request.
        BrokerCopyResult dirCopy = SandboxBroker.CopyDirRecursive(sourceFolder, stagingFolder);
        if (dirCopy.Outcome != BrokerOutcome.Ok)
        {
            SetStatus("Daemon-side emulator copy refused (" + dirCopy.Outcome + "); falling back to app-side traversal ...");
            CopyDirectoryRecursiveWithBroker(sourceFolder, stagingFolder);
        }
        token.ThrowIfCancellationRequested();

        // Everything downstream runs against stagingFolder. The PS2 ROM probe uses the staging
        // path too so a *.crack that was renamed in the emulator's own folder is still seen at
        // the same relative filename after the copy.
        if (_options is Ps2EmuOptions ps2Folder)
            ps2Folder.EmulatorFolder = stagingFolder;

        SetStatus("Writing config-file ...");
        ConfigFileEmitter.WriteConfig(stagingFolder, _options);
        NotifyRomFallback(_options);

        if (_options is Ps2EmuOptions ps2Opts)
            DeployPs2DiscImages(stagingFolder, ps2Opts, token);

        if (_options is Ps1EmuOptions ps1Opts)
            DeployPs1DiscImage(stagingFolder, ps1Opts, token);

        if (_options is PspEmuOptions pspOpts)
            DeployPspImage(stagingFolder, pspOpts, token);

        if (_options is Ps2EmuOptions ps2Ws && ps2Ws.UseWidescreenPatch)
            WriteWidescreenPatch(stagingFolder, ps2Ws);

        // Per-game LUA / TXT / PS3 config files. PS1 and PSP inline the user's TxtConfigPath
        // straight into the emitted config file, so no side-car copy is needed on those flows.
        if (_options is Ps2EmuOptions ps2Cfg)
        {
            WritePs2ExtraConfigs(stagingFolder, ps2Cfg);
            // WritePs2ExtraConfigs sets opts.ConfigLocalLua only when a per-game LUA was actually
            // written to /app0/patches (either the user picked LuaConfigPath or the database
            // resolved one). The emitted LUA's require() search path is fixed inside every PS2
            // emulator eboot to /app0/lua_include/?.lua, so a missing shared-helper folder here
            // means the LUA aborts at load and the emulator either falls back to defaults or
            // crashes with no upstream diagnostic. Fail up front with a specific reason instead
            // of letting the install-then-launch chain surface a mystery boot failure.
            if (!string.IsNullOrEmpty(ps2Cfg.ConfigLocalLua))
            {
                string luaSource = ConfigDatabase.GetLuaIncludePath();
                if (!DirectoryExistsWithBroker(luaSource))
                    throw new InvalidOperationException(BuildMissingLuaIncludeMessage(luaSource));
            }
            DeployPs2LuaInclude(stagingFolder);
        }

        token.ThrowIfCancellationRequested();

        // Mutate the staging folder's sce_sys/param.sfo so the shell registers the module under
        // the backup's own title, title id, and content id. Only the three game-identity fields
        // change; every format field the shell parses to accept the module (APP_TYPE, CATEGORY,
        // FORMAT, PARENTAL_LEVEL, ...) stays as the emulator author shipped it. A backup with no
        // derivable 9-character title id keeps the emulator's own id in the SFO (targetTitleId
        // has already picked it up above), so the install still lands under a valid id.
        string titleId = targetTitleId;
        if (BackupSfoRewriter.ApplyBackupIdentity(stagingFolder, _backup, out string backupTid))
        {
            SetStatus("Rewrote param.sfo for " + backupTid);
            titleId = backupTid;
        }

        // Regenerate every content-id-bound file in staging/sce_sys and fill in any missing
        // shell-visible image asset. ONLY runs for PS2 backups: the PS2 emulator folders that
        // ship under this project were assembled from stripped-down fake-package extractions
        // that do not carry a shell-complete sce_sys (no keystone, no license, no playgo, no
        // right.sprx), and the offline batch preparation the project ships fills those in with
        // orbis-pub-cmd img_create output that is bound to the emulator's own content id. When
        // the launch rewrites the SFO to the backup's own content id, license.info /
        // license.dat / playgo-chunk.dat go stale and the shell rejects the install; the
        // regenerator refreshes those files to keep them consistent.
        //
        // PS1 and PSP backups run on the dedicated ps1hd / psphd emulator folders, which are
        // fully-signed retail-shape fpkgs shipping their own keystone (bound to a per-title
        // passcode), license.info / license.dat signed against the folder's own content id,
        // and every other file a normally-installed fpkg carries. A regenerator pass on those
        // shapes overwrites four self-consistent files with debug-signed replacements the
        // shell's install pass then rejects; the whole title stops installing. Never touch
        // those folders.
        if (_backup.Platform == GamePlatform.PS2)
        {
            string newContentId = BackupSfoRewriter.ComposeContentId(_backup.Platform, titleId);
            SetStatus("Refreshing sce_sys for " + titleId + " ...");
            SceSysPrepareResult sceSysResult = SceSysRegenerator.PrepareStaging(
                stagingFolder, newContentId, _backup.Platform);
            if (sceSysResult.LicenseInfoWritten || sceSysResult.LicenseDatWritten
                || sceSysResult.PlayGoChunkPatched || sceSysResult.KeystoneWritten
                || sceSysResult.AssetsWritten > 0)
            {
                SetStatus("sce_sys refreshed (assets=" + sceSysResult.AssetsWritten
                    + " keystone=" + sceSysResult.KeystoneWritten
                    + " license=" + (sceSysResult.LicenseInfoWritten && sceSysResult.LicenseDatWritten)
                    + " playgo=" + sceSysResult.PlayGoChunkPatched + ")");
            }
        }

        // Sanity-check the staging folder before the install pass. A launch that installs a
        // half-populated folder can strand the shell's app database in a state the next launch
        // cannot recover from; this check catches the common failure modes (missing eboot,
        // missing sce_sys) up front so the caller sees a specific reason instead of a kernel-log
        // failure at broker step 17.
        VerifyStagingReady(stagingFolder);

        _launchedFolder = stagingFolder;

        // ---- Phase (b): install ----
        SetStatus("Installing " + titleId + " ...");
        var installProgress = new Progress<string>(step => SetStatus("Installing " + titleId + " - " + step));
        InstallResult install = BackupInstaller.Install(stagingFolder, titleId, installProgress, token);
        token.ThrowIfCancellationRequested();
        if (!install.Success)
        {
            string detail = "Install failed at step " + install.FailedStep
                + " (" + install.StepName + "): " + install.ErrorMessage;
            if (!string.IsNullOrEmpty(install.RawKlog))
                detail += "\n\nKernel log: " + install.RawKlog;
            throw new InvalidOperationException(detail);
        }

        string resolvedId = string.IsNullOrEmpty(install.TitleId) ? titleId : install.TitleId;

        // ---- Phase (c): launch ----
        SetStatus("Launching " + resolvedId + " ...");
        var launchProgress = new Progress<string>(step => SetStatus("Launching " + resolvedId + " - " + step));
        LaunchResult launch = BackupLauncher.Launch(resolvedId, stagingFolder, launchProgress);

        // BackupLauncher.Launch spins in ProcessExit.Exit on the successful path so control does
        // not reach the following lines. A return here means Launch handed back a Failed result
        // ahead of the arm call - a pid conflict, a mount that could not be re-bound, a foreground
        // user query that came back invalid, or the arm-worker spawn itself refused.
        if (!launch.Started)
            throw new InvalidOperationException(
                "Launch failed at " + launch.FailedStep + ": " + launch.ErrorMessage);
    }

    // Every backup launch stages its own per-title folder under this root. The root lives on
    // /data so the daemon-widened view reaches it; a subfolder per title id keeps two backups
    // from stepping on each other, and lets an uninstall wipe the per-title state without
    // touching the shared emulator source folder or any other backup's staging.
    private const string StagingFolderRoot = "/data/homebrew/games";

    // Picks the 9-character title id the staging folder + install pass will use. Uses the
    // backup's own game id when the scanner classified it (turned into an NP-title 9-char slug
    // with dashes / dots stripped) and falls back to whatever the emulator carries when the
    // backup slot is empty. Returns the empty string when neither source yields an id.
    private string ResolveTargetTitleId(string emulatorFolder)
    {
        string np = BackupSfoRewriter.PredictTitleId(_backup);
        if (!string.IsNullOrEmpty(np))
            return np;
        string? fromEmu = EmulatorRegistry.ReadTitleIdFromFolder(emulatorFolder);
        if (!string.IsNullOrEmpty(fromEmu))
            return fromEmu!;
        return _emu.TitleId ?? string.Empty;
    }

    // Barrier check the install pass runs against: a launch that fires against a folder missing
    // eboot.bin or sce_sys/param.sfo is guaranteed to strand the shell mid-registration, so the
    // failure has to surface here (with a specific reason) instead of at broker step 17. The
    // check goes through the broker for the /data-served staging path so a direct-call EINVAL
    // does not read as a missing file.
    private void VerifyStagingReady(string stagingFolder)
    {
        string root = stagingFolder.TrimEnd('/');
        string[] required =
        [
            root + "/eboot.bin",
            root + "/sce_sys/param.sfo",
        ];
        foreach (string path in required)
        {
            if (!FileExistsWithBroker(path))
                throw new InvalidOperationException(
                    "Staging is missing " + path + " - the launch would install a half-populated "
                    + "folder. Aborting before the shell registers a bad module.");
        }

        switch (_emu.Kind)
        {
            case EmulatorKind.PS1:
                if (!DirectoryExistsWithBroker(root + "/data"))
                    throw new InvalidOperationException(
                        "Staging is missing /data - PS1 launches read discs from data/discN.bin.");
                if (!FileExistsWithBroker(root + "/config-title.txt"))
                    throw new InvalidOperationException(
                        "Staging is missing config-title.txt - the PS1 emulator would boot with no image.");
                break;
            case EmulatorKind.PS2:
                if (!DirectoryExistsWithBroker(root + "/image"))
                    throw new InvalidOperationException(
                        "Staging is missing /image - PS2 launches read discs from image/discNN.iso.");
                if (!FileExistsWithBroker(root + "/config-emu-ps4.txt"))
                    throw new InvalidOperationException(
                        "Staging is missing config-emu-ps4.txt - the PS2 emulator would boot with no image.");
                break;
            case EmulatorKind.PSP:
                if (!FileExistsWithBroker(root + "/data/USER_L0.IMG"))
                    throw new InvalidOperationException(
                        "Staging is missing data/USER_L0.IMG - the PSP emulator would boot with no image.");
                if (!FileExistsWithBroker(root + "/config-title.txt"))
                    throw new InvalidOperationException(
                        "Staging is missing config-title.txt - the PSP emulator would boot with no image.");
                break;
        }
    }

    // ------------------------------------------------------------------
    //  PS2 disc image deployment
    // ------------------------------------------------------------------

    private void DeployPs2DiscImages(string emuFolder, Ps2EmuOptions opts, CancellationToken token)
    {
        string imageDir = emuFolder.TrimEnd('/') + "/image";
        EnsureDirectory(imageDir);

        if (opts.ImagePathList.Count > 0)
        {
            for (int i = 0; i < opts.ImagePathList.Count && i < 5; i++)
            {
                string src = opts.ImagePathList[i];
                if (string.IsNullOrEmpty(src) || !FileExistsWithBroker(src))
                    continue;
                string dest = imageDir + "/disc" + (i + 1).ToString("D2") + ".iso";
                CopyDiscWithProgress(src, dest, "Deploying disc " + (i + 1), token);
            }
            return;
        }

        // Single disc from the backup file.
        if (!string.IsNullOrEmpty(_backup.FilePath) && FileExistsWithBroker(_backup.FilePath))
        {
            string dest = imageDir + "/disc01.iso";
            CopyDiscWithProgress(_backup.FilePath, dest, "Deploying disc 1", token);
        }
    }

    // ------------------------------------------------------------------
    //  PS1 disc image deployment
    // ------------------------------------------------------------------

    private void DeployPs1DiscImage(string emuFolder, Ps1EmuOptions opts, CancellationToken token)
    {
        string dataDir = emuFolder.TrimEnd('/') + "/data";
        EnsureDirectory(dataDir);

        // The emulator reads discs as data/disc1.bin, data/disc2.bin, ... regardless of the disc
        // count. A single-disc backup lands as data/disc1.bin, which the emulator reads exactly
        // the same way it reads the first disc of a multi-disc set.
        static string DiscName(int index) => "data/disc" + (index + 1) + ".bin";
        static string CueName(int index) => "data/disc" + (index + 1) + ".cue";

        if (opts.DiscPaths.Count > 0)
        {
            int discCount = opts.DiscPaths.Count;
            for (int i = 0; i < discCount; i++)
            {
                string src = opts.DiscPaths[i];
                if (string.IsNullOrEmpty(src) || !src.StartsWith("/", StringComparison.Ordinal))
                    continue;
                if (!FileExistsWithBroker(src))
                    continue;
                string discRelative = DiscName(i);
                string dest = emuFolder.TrimEnd('/') + "/" + discRelative;
                CopyDiscWithProgress(src, dest, "Deploying disc " + (i + 1), token);
                CopyPs1CueSidecar(src, emuFolder.TrimEnd('/') + "/" + CueName(i), i + 1, token);
                opts.DiscPaths[i] = discRelative;
            }
            // Re-emit config so it has the disc paths.
            ConfigFileEmitter.WriteConfig(emuFolder, opts);
            return;
        }

        if (string.IsNullOrEmpty(_backup.FilePath) || !FileExistsWithBroker(_backup.FilePath))
            return;

        string src0;
        // When the backup itself is a .cue, that cue file IS the sidecar for the resolved .bin; the
        // sidecar copy below prefers the original cue over probing for a same-basename sibling of
        // the extracted .bin (which usually does not exist in a multi-track set).
        string? explicitCuePath = null;
        if (_backup.FilePath.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
        {
            CueSheet? cue = TryParseCue(_backup.FilePath);
            if (cue is null)
                throw new InvalidDataException($"Cannot parse cue: {_backup.FilePath}");

            List<string> binPaths = cue.GetBinFilePathsResolved(_backup.FilePath, FileExistsWithBroker);
            src0 = binPaths.Count > 0 ? binPaths[0] : "";
            explicitCuePath = _backup.FilePath;
        }
        else
        {
            src0 = _backup.FilePath;
        }

        if (string.IsNullOrEmpty(src0) || !FileExistsWithBroker(src0))
            throw new FileNotFoundException($"PS1 disc bytes not found: {src0}");

        string relative = DiscName(0);
        string destPath = emuFolder.TrimEnd('/') + "/" + relative;
        CopyDiscWithProgress(src0, destPath, "Deploying disc 1", token);

        string cueDest = emuFolder.TrimEnd('/') + "/" + CueName(0);
        if (explicitCuePath is not null)
            CopyPs1CueByPath(explicitCuePath, cueDest, token);
        else
            CopyPs1CueSidecar(src0, cueDest, 1, token);

        opts.DiscPaths.Add(relative);
        ConfigFileEmitter.WriteConfig(emuFolder, opts);
    }

    // Copies the .cue file that sits next to <paramref name="binSource"/> (same basename, .cue
    // extension) to <paramref name="cueDest"/>. Case sensitivity follows the source system: the
    // sibling is probed first with the exact source case, then, when the direct probe misses on a
    // case-insensitive file system that answered the .bin, an all-caps and all-lower fallback lets
    // a rip that reversed the case of the extension still resolve. A missing sibling is treated as
    // a single-track backup that did not ship a cue and the copy is skipped; the .bin path is
    // still deployed and the emulator is expected to read the disc raw.
    private void CopyPs1CueSidecar(string binSource, string cueDest, int discNumber,
        CancellationToken token)
    {
        int dot = binSource.LastIndexOf('.');
        int slash = binSource.LastIndexOf('/');
        // A source path without an extension (or where the last dot is inside a folder name)
        // cannot carry a sibling; the emulator's data/discN.bin still gets deployed above.
        if (dot < 0 || dot <= slash)
        {
            SetStatus("Disc " + discNumber + ": single-track PS1 backup, no cue file");
            return;
        }

        string stem = binSource.Substring(0, dot);
        string[] candidates =
        [
            stem + ".cue",
            stem + ".CUE",
            stem + ".Cue",
        ];

        foreach (string candidate in candidates)
        {
            if (FileExistsWithBroker(candidate))
            {
                CopyPs1CueByPath(candidate, cueDest, token);
                return;
            }
        }

        SetStatus("Disc " + discNumber + ": single-track PS1 backup, no cue file");
    }

    // Copies a cue file byte-exact through the same broker-cascade the .bin copy uses. Delegates
    // to CopyDiscWithProgress so both files travel over the same code path and the on-screen
    // progress bar continues to move; the caption names the cue role for anyone watching.
    private void CopyPs1CueByPath(string cueSource, string cueDest, CancellationToken token)
    {
        CopyDiscWithProgress(cueSource, cueDest, "Deploying cue file", token);
    }

    private static CueSheet? TryParseCue(string cuePath)
    {
        try
        {
            CueSheet? result = CueSheet.ParseFromFile(cuePath);
            if (result is not null)
                return result;
        }
        catch { }

        // Direct parse returned null or failed. Try via broker for /user or /data paths.
        if (SandboxBroker.IsOnBrokerPartition(cuePath) && SandboxBroker.IsReachable())
        {
            if (!SandboxBroker.FileExists(cuePath))
                return null;
            var (outcome, bytes) = SandboxBroker.ReadAllBytes(cuePath);
            if (outcome == BrokerOutcome.Ok && bytes.Length > 0)
            {
                string content = Encoding.UTF8.GetString(bytes);
                return CueSheet.Parse(content);
            }
        }
        return null;
    }

    // ------------------------------------------------------------------
    //  PSP image deployment
    // ------------------------------------------------------------------

    private void DeployPspImage(string emuFolder, PspEmuOptions opts, CancellationToken token)
    {
        if (string.IsNullOrEmpty(_backup.FilePath) || !FileExistsWithBroker(_backup.FilePath))
            return;

        string dataDir = emuFolder.TrimEnd('/') + "/data";
        EnsureDirectory(dataDir);

        string dest = dataDir + "/USER_L0.IMG";
        CopyDiscWithProgress(_backup.FilePath, dest, "Deploying UMD image", token);
        opts.ImagePath = "data/USER_L0.IMG";

        // Retail UMDs ship EBOOT.BIN wrapped in a ~PSP container the emulator cannot execute
        // directly. The block below extracts the wrapped EBOOT, asks the SDK's PSP EBOOT
        // decryptor to unwrap it, and (on success) re-embeds the plaintext PRX in the copied
        // USER_L0.IMG at the offset the encrypted entry sits at. TryDecrypt runs the KIRK
        // Cmd1 pipeline against the container's tag; when the tag is not in the seed table
        // or a KIRK integrity check fails, it returns false and the copy stays untouched. A
        // homebrew UMD that never carried the ~PSP wrapper takes the same silent skip path.
        TryDecryptAndReembedPspEboot(dest, token);
    }

    // Extracts PSP_GAME/SYSDIR/EBOOT.BIN from the freshly copied USER_L0.IMG, asks the SDK's
    // PSP EBOOT decryptor to unwrap it, and re-embeds the decrypted PRX in place when the
    // decrypt succeeds. Every non-success path (no EBOOT entry, no ~PSP wrap, unknown tag,
    // KIRK command 1 body decrypt unavailable) is a silent skip that leaves the copied ISO
    // byte-for-byte the way the previous copy landed it.
    private void TryDecryptAndReembedPspEboot(string userImgPath, CancellationToken token)
    {
        const int MaxEbootBytes = 32 * 1024 * 1024;

        byte[]? encryptedEboot;
        using (RandomAccessByteSource? isoSource = RandomAccessByteSource.Open(userImgPath))
        {
            if (isoSource is null)
            {
                SetStatus("PSP EBOOT: cannot re-open USER_L0.IMG for decrypt scan");
                return;
            }
            byte[]? entry = IsoReader.ReadFile(isoSource, "PSP_GAME/SYSDIR/EBOOT.BIN");
            if (entry is null)
            {
                SetStatus("PSP EBOOT: no encrypted EBOOT - skip decrypt");
                return;
            }
            if (entry.Length > MaxEbootBytes)
            {
                SetStatus("PSP EBOOT: entry larger than the "
                    + (MaxEbootBytes / (1024 * 1024)) + " MiB cap; skip decrypt");
                return;
            }
            encryptedEboot = entry;
        }

        token.ThrowIfCancellationRequested();

        // A file that does not start with the ~PSP magic is a homebrew EBOOT the emulator can
        // already run without a decrypt pass; the same holds for a truncated file the SDK's
        // TryPeekTag refuses. TryDecrypt would fail on both paths as well; probing the tag
        // first lets the "no encrypted EBOOT" breadcrumb call out the difference from the
        // "decrypt not available" one below.
        if (!SharpProspero.Compression.PspEbootDecryptor.TryPeekTag(encryptedEboot, out uint tag))
        {
            SetStatus("PSP EBOOT: not a ~PSP container - skip decrypt");
            return;
        }

        // The decrypt walks the KIRK engine + AMCTRL pipeline over the wrapped EBOOT. A tag the
        // table does not carry, a truncated buffer, or a KIRK dispatch that trips an internal
        // range check must not tear down the launch chain: the emulator can still boot the
        // encrypted image in the failure case (it will surface its own error), and swallowing
        // the exception lets a homebrew EBOOT or a title whose keys are not in the table launch
        // through the same code path as a title whose keys are. The catch reports the exception
        // message so a debug pass on the device log still sees what went wrong.
        byte[] decrypted;
        bool decryptedOk;
        try
        {
            decryptedOk = SharpProspero.Compression.PspEbootDecryptor.TryDecrypt(
                encryptedEboot, out decrypted);
        }
        catch (Exception decryptEx)
        {
            SetStatus("PSP EBOOT tag 0x" + tag.ToString("X8")
                + ": decrypt threw " + decryptEx.GetType().Name + " (" + decryptEx.Message
                + ") - shipping encrypted image");
            return;
        }
        if (!decryptedOk)
        {
            SetStatus("PSP EBOOT tag 0x" + tag.ToString("X8")
                + ": decrypt returned false - retail UMD may not launch");
            return;
        }

        token.ThrowIfCancellationRequested();

        // The offset the encrypted EBOOT sits at inside USER_L0.IMG is not carried out of the
        // ISO walker, so a needle search over the copied image finds it. Cap the needle at
        // 500 KB of the encrypted EBOOT body (or the entire EBOOT when it is shorter): a
        // window that wide has no realistic collision chance inside a ~1 GB UMD image, while
        // a short prefix built from ~PSP-header bytes could false-match on unrelated ISO data
        // and cause the decrypted overwrite to trash the wrong region of the image.
        const int NeedleLength = 512320;
        int needleLen = Math.Min(NeedleLength, encryptedEboot.Length);
        byte[] needle = new byte[needleLen];
        Array.Copy(encryptedEboot, 0, needle, 0, needleLen);

        long ebootOffset = FindNeedleInFile(userImgPath, needle, token);
        if (ebootOffset < 0)
        {
            SetStatus("PSP EBOOT: entry bytes not found in USER_L0.IMG; skip re-embed");
            return;
        }

        // Overwrite in place: allocate a buffer sized to the encrypted region so a shorter
        // decrypted PRX zero-pads the trailing gap and the ISO's file-entry length stays
        // correct. A longer output would run into the next ISO entry and corrupt the image,
        // so refuse only in that case.
        if (decrypted.Length > encryptedEboot.Length)
        {
            SetStatus("PSP EBOOT: decrypted length " + decrypted.Length + " > encrypted "
                + encryptedEboot.Length + "; skip re-embed to avoid ISO corruption");
            return;
        }

        byte[] writeBuffer;
        if (decrypted.Length == encryptedEboot.Length)
        {
            writeBuffer = decrypted;
        }
        else
        {
            writeBuffer = new byte[encryptedEboot.Length];
            Buffer.BlockCopy(decrypted, 0, writeBuffer, 0, decrypted.Length);
            // remaining bytes stay 0 (default for byte[]).
        }

        WriteFileRangeWithBroker(userImgPath, ebootOffset, writeBuffer);
        SetStatus("PSP EBOOT decrypted and re-embedded (" + decrypted.Length + " plaintext + "
            + (encryptedEboot.Length - decrypted.Length) + " pad bytes)");
    }

    // Locates <paramref name="needle"/> inside <paramref name="path"/> and returns its offset,
    // or -1 when the needle does not appear. Reads through the same broker-cascade the disc
    // deploy uses so a file under /data/homebrew answers even when the module's mount namespace
    // does not bind the partition. Chunk boundaries carry a needle-length overlap so a match
    // that spans two chunks still resolves.
    private static long FindNeedleInFile(string path, ReadOnlySpan<byte> needle,
        CancellationToken token)
    {
        if (needle.Length == 0)
            return -1;

        using RandomAccessByteSource? source = RandomAccessByteSource.Open(path);
        if (source is null)
            return -1;

        long size = source.Size;
        if (size < needle.Length)
            return -1;

        const int ChunkSize = 4 * 1024 * 1024;
        byte[] buffer = new byte[ChunkSize + needle.Length - 1];
        long offset = 0;
        int overlap = 0;

        byte[] needleArr = needle.ToArray();
        while (offset < size)
        {
            token.ThrowIfCancellationRequested();
            long remaining = size - offset;
            int want = (int)Math.Min(ChunkSize, remaining);
            int read = ReadFullyAt(source, offset, buffer.AsSpan(overlap, want));
            if (read <= 0)
                return -1;

            int usable = overlap + read;
            int limit = usable - needleArr.Length + 1;
            for (int i = 0; i < limit; i++)
            {
                bool match = true;
                for (int j = 0; j < needleArr.Length; j++)
                {
                    if (buffer[i + j] != needleArr[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return offset - overlap + i;
            }

            // Preserve the last needle-1 bytes so a match straddling this chunk's tail and the
            // next chunk's head still gets caught by the following iteration's compare loop.
            overlap = needleArr.Length - 1;
            if (overlap > usable)
                overlap = usable;
            if (overlap > 0)
                Buffer.BlockCopy(buffer, usable - overlap, buffer, 0, overlap);
            offset += read;
        }
        return -1;
    }

    // Reads exactly buffer.Length bytes from <paramref name="source"/> at <paramref name="offset"/>,
    // paging as needed. Returns the total number of bytes filled; a value less than the request
    // means the source hit end-of-file, and a negative return from the underlying ReadAt aborts.
    private static int ReadFullyAt(RandomAccessByteSource source, long offset, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = source.ReadAt(offset + total, buffer[total..]);
            if (n < 0)
                return total > 0 ? total : n;
            if (n == 0)
                break;
            total += n;
        }
        return total;
    }

    // Overwrites bytes at <paramref name="offset"/> in <paramref name="path"/>, routing through
    // the broker for paths on partitions the mount namespace does not bind. The direct route
    // opens for random-access read-write with FileMode.Open so the surrounding bytes of the file
    // stay intact; the broker route uses WriteRange, which has the same semantics.
    private static void WriteFileRangeWithBroker(string path, long offset, ReadOnlySpan<byte> data)
    {
        try
        {
            using DeviceFileStream stream = DeviceFileStream.Open(path, FileMode.Open, FileAccess.ReadWrite);
            stream.WriteAt(offset, data);
            stream.Flush();
            return;
        }
        catch
        {
            if (!SandboxBroker.IsOnBrokerPartition(path) || !SandboxBroker.IsReachable())
                throw;
        }

        BrokerOutcome outcome = SandboxBroker.WriteRange(path, offset, data);
        if (outcome != BrokerOutcome.Ok)
            throw new IOException("Broker write failed for " + path + ": " + outcome);
    }

    // ------------------------------------------------------------------
    //  Widescreen patch
    // ------------------------------------------------------------------

    private void WriteWidescreenPatch(string emuFolder, Ps2EmuOptions opts)
    {
        string gameId = opts.Ps2TitleId;
        if (string.IsNullOrEmpty(gameId))
            return;

        // Widescreen patches are indexed by the boot ELF's 32-bit XOR checksum, formatted as
        // eight uppercase hex characters ("0001171A.lua"). Compute the checksum from the disc
        // image so the database look-up runs against the same key its files are named by; a disc
        // image that cannot be opened or does not carry a resolvable BOOT2 line yields no key and
        // the branch stays a silent no-op. The destination file name still uses the hyphenated
        // disc serial ("SLES-12345_trophies.lua") because that is the shape the emulator reads.
        string? crc = Ps2ElfCrc.ComputeFromIso(_backup.FilePath);
        if (string.IsNullOrEmpty(crc))
        {
            SetStatus("Widescreen: could not compute ELF CRC; skipping.");
            return;
        }

        string? patchContent = ConfigDatabase.ReadWidescreenPatch(crc);
        if (patchContent is null)
            return;

        string fullId = NormaliseHyphenId(gameId);
        string trophyDir = emuFolder.TrimEnd('/') + "/trophy_data";
        EnsureDirectory(trophyDir);

        string luaPath = trophyDir + "/" + fullId + "_trophies.lua";
        WriteAllTextWithBroker(luaPath, patchContent);

        opts.PathTrophyData = "/app0/trophy_data";
    }

    // ------------------------------------------------------------------
    //  PS2 extra configs (LUA / TXT / PS3 / widescreen / memory card)
    // ------------------------------------------------------------------

    // Writes every per-game side file a PS2 launch drops into the emulator folder before the
    // install pass: user-supplied and database-resolved TXT/LUA configs, the PS3 lopnor
    // compatibility block, and an optional user-supplied memory card. Widescreen writes go
    // through WriteWidescreenPatch on a separate branch above; this method covers everything
    // else.
    //
    // Database files are keyed by the raw disc serial with underscore and dot ("SLUS_205.62");
    // per-game files land under the hyphen/no-dot form ("SLUS-20562"). Both forms are derived
    // from opts.Ps2TitleId here so a caller that only knows the hyphenated form still resolves
    // the right files.
    //
    // Layout (loaded via /app0 once the module launches):
    //   patches/<FullId>_config.lua                          - user LUA / DB LUA
    //   patches/<FullId>/<FullId>_lopnor.cfgbin              - PS3 compatibility config
    //   feature_data/<FullId>/custom.card                    - user memory card
    //   config-emu-ps4.txt                                   - DB TXT is appended inline via
    //                                                          opts.DatabaseTxtContent + re-emit
    private void WritePs2ExtraConfigs(string emuFolder, Ps2EmuOptions opts)
    {
        string hyphenatedId = opts.Ps2TitleId;
        if (string.IsNullOrEmpty(hyphenatedId))
            return;

        string fullId = NormaliseHyphenId(hyphenatedId);
        string rawId = HyphenToRaw(hyphenatedId);
        string root = emuFolder.TrimEnd('/');

        // TXT config. User-supplied TXT is appended inline by EmitPs2 via AppendUserTxtConfig; DB
        // TXT lands on the options as DatabaseTxtContent so the re-emit below rolls it into the
        // same config-emu-ps4.txt file. Each block is preceded by a #-comment header so the
        // emulator sees one file with two override sources plainly labelled.
        if (string.IsNullOrEmpty(opts.TxtConfigPath))
        {
            string? dbTxt = ConfigDatabase.ReadTxtConfig(rawId);
            if (dbTxt is not null)
                opts.DatabaseTxtContent = dbTxt;
        }

        // LUA config. Either a user-supplied file or a database entry lands at
        // patches/<FullId>_config.lua directly (no subfolder). path-patches and config-local-lua
        // both point at this file so the emulator picks it up regardless of which flag it reads.
        string luaContent = ResolveLuaContent(opts, rawId);
        if (!string.IsNullOrEmpty(luaContent))
        {
            string luaDest = root + "/patches/" + fullId + "_config.lua";
            EnsureDirectory(root + "/patches");
            WriteAllTextWithBroker(luaDest, luaContent);
            opts.PathPatches = "/app0/patches";
            opts.ConfigLocalLua = "/app0/patches/" + fullId + "_config.lua";
        }

        // PS3 compatibility config (lopnor). The file lands at
        // patches/<FullId>/<FullId>_lopnor.cfgbin - a per-title sub-folder under patches - and the
        // emulator picks it up when --lopnor-config=1 is in the config file.
        if (opts.EnableLopnorConfig)
        {
            string? ps3Path = ConfigDatabase.GetPs3ConfigPath(rawId);
            if (ps3Path is not null)
            {
                string ps3Dir = root + "/patches/" + fullId;
                EnsureDirectory(ps3Dir);
                string dest = ps3Dir + "/" + fullId + "_lopnor.cfgbin";
                CopyDiscWithProgress(ps3Path, dest, "Copying PS3 config", default);
                opts.PathPatches = "/app0/patches";
            }
        }

        // User memory card. The user-picked file lands at feature_data/<FullId>/custom.card;
        // the emulator reads it as its per-title card image.
        if (!string.IsNullOrEmpty(opts.MemoryCardPath) && FileExistsWithBroker(opts.MemoryCardPath))
        {
            string featureDir = root + "/feature_data/" + fullId;
            EnsureDirectory(featureDir);
            string dest = featureDir + "/custom.card";
            CopyDiscWithProgress(opts.MemoryCardPath, dest, "Copying memory card", default);
            opts.PathFeatureData = "/app0/feature_data";
        }

        // Re-emit the config so any option field the branches above set (LUA path, database TXT,
        // patches root, feature-data root) lands in the file the emulator reads. The emit is
        // idempotent; the earlier initial emit is overwritten byte-for-byte with the same base
        // flags plus the newly resolved additions.
        ConfigFileEmitter.WriteConfig(emuFolder, opts);
    }

    // Resolves the LUA content to write, preferring a user-supplied file over a database entry.
    // Returns the empty string when neither route yields content so the caller can skip the write.
    private static string ResolveLuaContent(Ps2EmuOptions opts, string rawId)
    {
        if (!string.IsNullOrEmpty(opts.LuaConfigPath))
        {
            try
            {
                if (FileExistsWithBroker(opts.LuaConfigPath))
                {
                    if (FileSystem.Exists(opts.LuaConfigPath))
                        return FileSystem.ReadAllText(opts.LuaConfigPath);
                    if (SandboxBroker.IsOnBrokerPartition(opts.LuaConfigPath) && SandboxBroker.IsReachable())
                    {
                        var (outcome, bytes) = SandboxBroker.ReadAllBytes(opts.LuaConfigPath);
                        if (outcome == BrokerOutcome.Ok && bytes.Length > 0)
                            return Encoding.UTF8.GetString(bytes);
                    }
                }
            }
            catch (Exception) { }
        }
        return ConfigDatabase.ReadLuaConfig(rawId) ?? string.Empty;
    }

    // Reduces a hyphenated disc serial ("SLUS-20562") to itself; a call site that passes the
    // raw form ("SLUS_205.62") gets the hyphenated, no-dot form back. The helper is idempotent
    // so a caller that runs the same options twice through the extra-config path never doubles
    // up separators.
    private static string NormaliseHyphenId(string id)
    {
        if (string.IsNullOrEmpty(id))
            return string.Empty;
        var sb = new StringBuilder(id.Length + 1);
        int letters = 0;
        foreach (char c in id)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                if (letters < 4)
                {
                    sb.Append(char.ToUpperInvariant(c));
                    letters++;
                }
            }
            else if (c is >= '0' and <= '9')
            {
                if (letters == 4)
                {
                    sb.Append('-');
                    letters++;
                }
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    // Rebuilds the raw disc-serial form the database files key by ("SLUS-20562" -> "SLUS_205.62")
    // so a per-game database lookup runs against the same key the database uses. Handles both
    // already-raw and hyphenated inputs; the letters and digits are the source of truth and any
    // existing separators are dropped before the new ones are inserted.
    private static string HyphenToRaw(string id)
    {
        if (string.IsNullOrEmpty(id))
            return string.Empty;

        var letters = new StringBuilder(4);
        var digits = new StringBuilder(5);
        foreach (char c in id)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                if (letters.Length < 4)
                    letters.Append(char.ToUpperInvariant(c));
            }
            else if (c is >= '0' and <= '9')
            {
                if (digits.Length < 5)
                    digits.Append(c);
            }
        }
        if (letters.Length != 4 || digits.Length != 5)
            return id;
        return letters + "_" + digits.ToString(0, 3) + "." + digits.ToString(3, 2);
    }

    // ------------------------------------------------------------------
    //  PS2 lua_include shared library
    // ------------------------------------------------------------------

    // Copies every file the shared lua_include library ships into <paramref name="emuFolder"/>'s
    // own lua_include/ directory so per-game LUA scripts that require() out of it resolve. Reads
    // route through the sandbox broker for partitions the mount namespace does not bind. When
    // the library is not present on device the copy surfaces a status message so the user knows
    // to stage the folder; the caller in Deploy has already thrown up front when a LUA that needs
    // the helpers was resolved, so reaching this point without the library is safe.
    private void DeployPs2LuaInclude(string emuFolder)
    {
        string source = ConfigDatabase.GetLuaIncludePath();
        if (!DirectoryExistsWithBroker(source))
        {
            SetStatus(BuildMissingLuaIncludeMessage(source));
            return;
        }

        SetStatus("Copying lua_include...");
        string destRoot = emuFolder.TrimEnd('/') + "/lua_include";
        EnsureDirectory(destRoot);
        CopyDirectoryRecursiveWithBroker(source, destRoot);
    }

    // Builds the diagnostic string that both the DeployPs2LuaInclude status update and the
    // fail-fast throw in Deploy share, so the two messages stay identical. Names the on-device
    // path the resolver settled on and the standard staging location so the user has both the
    // "what" and the "where" needed to fix the missing helper.
    private static string BuildMissingLuaIncludeMessage(string source)
        => "lua_include not found at " + source
           + "; per-game LUA patches that require shared helpers will not resolve."
           + " Copy the emulator-resources/lua_include folder to /data/homebrew/emulator-resources/"
           + " (or a USB path).";

    // Recursively copies every regular file under <paramref name="source"/> into
    // <paramref name="destination"/>, preserving the relative folder shape. Uses the broker
    // listing when the source lives on a partition the mount namespace does not bind and falls
    // through to the direct listing otherwise. Directory creation and per-file copy both go
    // through the broker-cascade helpers so a source under /data and a destination under /app0
    // (or vice versa) both resolve. Copies are byte-exact: the per-file inner call uses the
    // same DeviceFileStream/broker pipeline the disc deploy uses instead of the text-shaped
    // config-file wrapper, so a binary file dropped inside the tree survives intact.
    private static void CopyDirectoryRecursiveWithBroker(string source, string destination)
    {
        var pending = new Stack<(string Src, string Dst)>();
        pending.Push((source.TrimEnd('/'), destination.TrimEnd('/')));

        while (pending.Count > 0)
        {
            (string src, string dst) = pending.Pop();
            EnsureDirectory(dst);

            List<(string Name, bool IsDirectory)> entries = ListDirectoryWithBroker(src);
            foreach ((string name, bool isDirectory) in entries)
            {
                string childSrc = src + "/" + name;
                string childDst = dst + "/" + name;
                if (isDirectory)
                    pending.Push((childSrc, childDst));
                else
                    CopyRegularFileWithBroker(childSrc, childDst);
            }
        }
    }

    // Byte-exact file copy that mirrors the DeviceFileStream loop of CopyDiscWithProgress but
    // without the on-screen progress counters: lua_include ships hundreds of tiny files and a
    // per-file counter reset would flicker the shell. When either end lives on a broker-served
    // partition the copy falls through to a broker-paged loop that reads and writes in 4 MiB
    // chunks, the same way CopyDiscViaBroker handles the disc images.
    private static void CopyRegularFileWithBroker(string source, string destination)
    {
        try
        {
            using DeviceFileStream from = DeviceFileStream.OpenRead(source);
            using DeviceFileStream to = DeviceFileStream.Create(destination);
            const int BufferSize = 4 * 1024 * 1024;
            byte[] buffer = new byte[BufferSize];
            while (true)
            {
                int read = from.Read(buffer);
                if (read == 0)
                    break;
                to.Write(buffer.AsSpan(0, read));
            }
            return;
        }
        catch
        {
            bool srcBroker = SandboxBroker.IsOnBrokerPartition(source) && SandboxBroker.IsReachable();
            bool dstBroker = SandboxBroker.IsOnBrokerPartition(destination) && SandboxBroker.IsReachable();
            if (!srcBroker && !dstBroker)
                throw;
        }

        const int ChunkSize = 4 * 1024 * 1024;
        byte[] chunk = new byte[ChunkSize];
        long offset = 0;

        bool useBrokerSrc = SandboxBroker.IsOnBrokerPartition(source) && SandboxBroker.IsReachable();
        bool useBrokerDst = SandboxBroker.IsOnBrokerPartition(destination) && SandboxBroker.IsReachable();

        // Create or truncate the destination up front through the broker so the write loop
        // starts from a known-empty file when the destination is broker-served.
        if (useBrokerDst)
        {
            BrokerOutcome create = SandboxBroker.WriteRange(destination, 0, ReadOnlySpan<byte>.Empty);
            if (create != BrokerOutcome.Ok)
                throw new IOException("Cannot create destination through broker: " + create);
        }

        DeviceFileStream? directFrom = null;
        DeviceFileStream? directTo = null;
        try
        {
            if (!useBrokerSrc)
                directFrom = DeviceFileStream.OpenRead(source);
            if (!useBrokerDst)
                directTo = DeviceFileStream.Create(destination);

            while (true)
            {
                int read;
                if (useBrokerSrc)
                {
                    var br = SandboxBroker.ReadRange(source, offset, chunk.AsSpan());
                    if (br.Outcome != BrokerOutcome.Ok)
                        throw new IOException("Broker read failed for " + source + ": " + br.Outcome);
                    read = br.BytesRead;
                }
                else
                {
                    read = directFrom!.Read(chunk);
                }

                if (read == 0)
                    break;

                if (useBrokerDst)
                {
                    BrokerOutcome wo = SandboxBroker.WriteRange(destination, offset, chunk.AsSpan(0, read));
                    if (wo != BrokerOutcome.Ok)
                        throw new IOException("Broker write failed for " + destination + ": " + wo);
                }
                else
                {
                    directTo!.Write(chunk.AsSpan(0, read));
                }
                offset += read;
            }
        }
        finally
        {
            directFrom?.Dispose();
            directTo?.Dispose();
        }
    }

    // Lists <paramref name="folder"/> as (name, isDirectory) tuples, trying the direct listing
    // first and falling back to the broker when the path lives on a partition the module cannot
    // open directly. Returns an empty list when neither route succeeds.
    private static List<(string Name, bool IsDirectory)> ListDirectoryWithBroker(string folder)
    {
        var result = new List<(string, bool)>();
        if (FileSystem.TryEnumerateDirectory(folder, out IReadOnlyList<DirectoryEntry> entries, out _))
        {
            foreach (DirectoryEntry entry in entries)
            {
                if (entry.IsDirectory || entry.IsFile)
                    result.Add((entry.Name, entry.IsDirectory));
            }
            return result;
        }
        if (SandboxBroker.IsOnBrokerPartition(folder) && SandboxBroker.IsReachable())
        {
            var (outcome, brokerEntries) = SandboxBroker.List(folder);
            if (outcome == BrokerOutcome.Ok)
            {
                foreach (BrokerDirEntry entry in brokerEntries)
                {
                    if (entry.IsDirectory || entry.IsFile)
                        result.Add((entry.Name, entry.IsDirectory));
                }
            }
        }
        return result;
    }

    // Existence check for a directory that routes through the broker for paths on partitions the
    // mount namespace does not bind. Returns true only when the target is a directory: a file
    // that happens to share the path answers false so a caller that intends to copy children
    // does not try to descend into a regular file.
    private static bool DirectoryExistsWithBroker(string path)
    {
        if (FileSystem.IsDirectory(path))
            return true;
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            return SandboxBroker.IsDirectory(path);
        return false;
    }

    // Surfaces the last ROM-resolve note the PS2 options object produced. The lazy RomFile probe
    // sets the note when it fell back to the bundled default; when the note is present, notify
    // the shell so the user sees why the config lists the default instead of a folder-installed
    // *.crack. Called after every WriteConfig so an emit re-run also picks up the note.
    private void NotifyRomFallback(IEmuOptions options)
    {
        if (options is not Ps2EmuOptions ps2)
            return;
        string? note = ps2.LastRomResolveNote;
        if (string.IsNullOrEmpty(note))
            return;
        SetStatus(note);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    // Copies a disc image with periodic progress reporting and cancellation. A four-gigabyte PS2
    // ISO used to freeze the shell for minutes because CopyFile ran on the UI thread; this runs on
    // the background thread the caller is already on and lets the shell tick while it works.
    //
    // Every launch redeploys the disc, even when the destination path already carries a file of
    // the same size. Two different backups can be the same size (a common re-rip case), and there
    // is no on-disk cheap way to prove the destination bytes match the source bytes without
    // reading both files; skipping the copy on size alone silently ran the previous game when the
    // user asked for a new one.
    private void CopyDiscWithProgress(string source, string destination, string caption,
        CancellationToken token)
    {
        SetStatus(caption);

        long total = GetFileSizeWithBroker(source);
        Interlocked.Exchange(ref _launchTotal, total);
        Interlocked.Exchange(ref _launchDone, 0);

        // Daemon-side single-file copy when the destination lives on a broker-served partition
        // (every launch stages under /data, which qualifies). The daemon opens both ends itself
        // and streams the bytes through its own 8 MiB copy buffer, so a multi-GB PS2 ISO uses
        // one broker round trip in place of ~350K sockets the WriteRange loop below would open
        // in 2 KiB chunks. That exhausts the ephemeral TCP port range on the app side and is
        // what kills the app half-way through a large disc copy. Progress is reported at the
        // end of the call since the daemon does not stream mid-copy status back; the on-screen
        // total bar still shows the full expected size so the user sees the copy is running.
        bool dstBroker = SandboxBroker.IsOnBrokerPartition(destination) && SandboxBroker.IsReachable();
        if (dstBroker)
        {
            BrokerOutcome copyOutcome = SandboxBroker.CopyFile(source, destination);
            if (copyOutcome == BrokerOutcome.Ok)
            {
                Interlocked.Exchange(ref _launchDone, total);
                return;
            }
            SetStatus(caption + " - daemon copy refused (" + copyOutcome + "); falling back to chunked copy ...");
        }

        try
        {
            using DeviceFileStream from = DeviceFileStream.OpenRead(source);
            using DeviceFileStream to = DeviceFileStream.Create(destination);
            const int bufferSize = 4 * 1024 * 1024;
            byte[] buffer = new byte[bufferSize];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = from.Read(buffer);
                if (read == 0)
                    break;
                to.Write(buffer.AsSpan(0, read));
                Interlocked.Add(ref _launchDone, read);
            }
            return;
        }
        catch
        {
            bool srcBroker = SandboxBroker.IsOnBrokerPartition(source) && SandboxBroker.IsReachable();
            if (!srcBroker && !dstBroker)
                throw;
        }

        // Every daemon-side path refused and at least one end is on a broker-served partition.
        // Reset progress and re-copy through the broker in chunks. This is the last-resort path
        // and stays around for a case a future firmware refuses the daemon copy; in normal use
        // the CopyFile branch above completes before this ever runs.
        Interlocked.Exchange(ref _launchDone, 0);
        CopyDiscViaBroker(source, destination, token);
    }

    private void SetStatus(string status)
    {
        _launchStatus = status;
    }

    private static void EnsureDirectory(string path)
    {
        if (FileSystem.Exists(path))
            return;
        // The path may sit on a broker-served partition where direct calls do not reach.
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
        {
            if (!SandboxBroker.IsDirectory(path))
                SandboxBroker.MkdirRecursive(path);
            return;
        }
        FileSystem.CreateDirectoryRecursive(path);
    }

    private static string FormatMB(long bytes)
    {
        const long OneMB = 1024L * 1024L;
        if (bytes < OneMB * 1024)
            return (bytes / (double)OneMB).ToString("0.0") + " MB";
        return (bytes / (double)(OneMB * 1024)).ToString("0.00") + " GB";
    }

    /// <summary>
    /// Formats a game ID into the PS2 disc serial format with a hyphen
    /// (e.g. "SLES12345" to "SLES-12345").
    /// </summary>
    private static string FormatPs2TitleId(string gameId)
    {
        if (string.IsNullOrEmpty(gameId))
            return gameId;
        if (gameId.Contains('-'))
            return gameId;
        if (gameId.Length > 4)
            return gameId.Substring(0, 4) + "-" + gameId.Substring(4);
        return gameId;
    }

    /// <summary>
    /// Returns true when the file exists, checking through the broker when the path is on
    /// a broker-served partition and the direct probe returns false.
    /// </summary>
    private static bool FileExistsWithBroker(string path)
    {
        if (FileSystem.Exists(path))
            return true;
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            return SandboxBroker.FileExists(path);
        return false;
    }

    /// <summary>
    /// Writes text to a file, falling back to the broker when the path is on a broker-served
    /// partition and the direct write fails.
    /// </summary>
    private static void WriteAllTextWithBroker(string path, string content)
    {
        try
        {
            FileSystem.WriteAllText(path, content);
            return;
        }
        catch
        {
            if (!SandboxBroker.IsOnBrokerPartition(path) || !SandboxBroker.IsReachable())
                throw;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        BrokerOutcome outcome = SandboxBroker.WriteAllBytes(path, bytes);
        if (outcome != BrokerOutcome.Ok)
            throw new IOException("Broker write failed for " + path + ": " + outcome);
    }

    /// <summary>
    /// Copies a file from <paramref name="source"/> to <paramref name="destination"/>,
    /// falling back to the broker for either end when it sits on a broker-served partition
    /// and the direct copy fails.
    /// </summary>
    private static void CopyFileWithBroker(string source, string destination)
    {
        try
        {
            FileSystem.CopyFile(source, destination);
            return;
        }
        catch
        {
            bool srcBroker = SandboxBroker.IsOnBrokerPartition(source) && SandboxBroker.IsReachable();
            bool dstBroker = SandboxBroker.IsOnBrokerPartition(destination) && SandboxBroker.IsReachable();
            if (!srcBroker && !dstBroker)
                throw;
        }

        // Buffer the source through the broker when needed; these are small config files
        // so the full content fits in memory.
        byte[] data;
        if (SandboxBroker.IsOnBrokerPartition(source) && SandboxBroker.IsReachable())
        {
            var (outcome, bytes) = SandboxBroker.ReadAllBytes(source);
            if (outcome != BrokerOutcome.Ok)
                throw new IOException("Broker read failed for " + source + ": " + outcome);
            data = bytes;
        }
        else
        {
            string text = FileSystem.ReadAllText(source);
            data = Encoding.UTF8.GetBytes(text);
        }

        if (SandboxBroker.IsOnBrokerPartition(destination) && SandboxBroker.IsReachable())
        {
            BrokerOutcome wo = SandboxBroker.WriteAllBytes(destination, data);
            if (wo != BrokerOutcome.Ok)
                throw new IOException("Broker write failed for " + destination + ": " + wo);
        }
        else
        {
            FileSystem.WriteAllText(destination, Encoding.UTF8.GetString(data));
        }
    }

    /// <summary>
    /// Returns the size of the file at <paramref name="path"/>, falling back to the broker
    /// stat when the direct query fails and the path is on a broker-served partition.
    /// Returns zero when neither source can answer.
    /// </summary>
    private static long GetFileSizeWithBroker(string path)
    {
        try { return FileSystem.GetFileSize(path); }
        catch (ProsperoException)
        {
            if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            {
                BrokerStat stat = SandboxBroker.Stat(path);
                if (stat.Ok) return stat.Size;
            }
            return 0;
        }
    }

    /// <summary>
    /// Copies a disc image through the broker, reading and/or writing in 4 MB chunks.
    /// Called when at least one of the two paths sits on a broker-served partition and the
    /// direct DeviceFileStream path failed.
    /// </summary>
    private void CopyDiscViaBroker(string source, string destination, CancellationToken token)
    {
        bool srcBroker = SandboxBroker.IsOnBrokerPartition(source) && SandboxBroker.IsReachable();
        bool dstBroker = SandboxBroker.IsOnBrokerPartition(destination) && SandboxBroker.IsReachable();

        const int bufferSize = 4 * 1024 * 1024;
        byte[] buffer = new byte[bufferSize];
        long offset = 0;

        // When the destination is on a broker partition, create or truncate it with a zero-length
        // write at offset zero before the chunk loop begins.
        if (dstBroker)
        {
            BrokerOutcome create = SandboxBroker.WriteRange(destination, 0, ReadOnlySpan<byte>.Empty);
            if (create != BrokerOutcome.Ok)
                throw new IOException("Cannot create destination through broker: " + create);
        }

        DeviceFileStream? directFrom = null;
        DeviceFileStream? directTo = null;
        try
        {
            if (!srcBroker)
                directFrom = DeviceFileStream.OpenRead(source);
            if (!dstBroker)
                directTo = DeviceFileStream.Create(destination);

            while (true)
            {
                token.ThrowIfCancellationRequested();

                int read;
                if (srcBroker)
                {
                    var br = SandboxBroker.ReadRange(source, offset, buffer.AsSpan());
                    if (br.Outcome != BrokerOutcome.Ok)
                        throw new IOException("Broker read failed for " + source + ": " + br.Outcome);
                    read = br.BytesRead;
                }
                else
                {
                    read = directFrom!.Read(buffer);
                }

                if (read == 0)
                    break;

                if (dstBroker)
                {
                    BrokerOutcome wo = SandboxBroker.WriteRange(destination, offset,
                        buffer.AsSpan(0, read));
                    if (wo != BrokerOutcome.Ok)
                        throw new IOException("Broker write failed for " + destination + ": " + wo);
                }
                else
                {
                    directTo!.Write(buffer.AsSpan(0, read));
                }

                offset += read;
                Interlocked.Add(ref _launchDone, read);
            }
        }
        finally
        {
            directFrom?.Dispose();
            directTo?.Dispose();
        }
    }
}
