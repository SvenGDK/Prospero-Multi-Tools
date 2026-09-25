// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Data;
using ProsperoMultiTools.Emulator;
using ProsperoMultiTools.Shell;
using SharpProspero.Application;
using SharpProspero.Audio;
using SharpProspero.Graphics;
using SharpProspero.Interop;
using SharpProspero.Interop.Pad;
using SharpProspero.Media;
using SharpProspero.Platform;
using SharpProspero.Storage;
using SharpProspero.Storage.Sfo;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ProsperoMultiTools.Browser;

internal sealed class BackupDetailScreen : MultiToolsScreen
{
    private readonly BackupInfo _backup;
    private IDisposable? _decodedIcon;
    private IDisposable? _decodedBackground;
    private AudioQueueDevice? _audio;
    private MediaPlayer? _player;
    private bool _soundtrackPlaying;
    private bool _soundtrackUserStopped;
    private bool _iconLoaded;
    private int _selectedPs2EmuIndex;

    public BackupDetailScreen(MultiToolsShell shell, BackupInfo backup) : base(shell)
    {
        _backup = backup;
    }

    public override string Title => _backup.DisplayTitle;

    public override string Hint => "Triangle toggles soundtrack, Circle back.";

    protected override UiElement BuildRoot()
    {
        // Single-column details view: the cover art and background art sit at the top as fixed
        // tiles, then the scrolling metadata list and the action buttons fill the rest of the
        // width. Capping the cover at a 240x240 tile and the background at 480x270 keeps a
        // 512x512 icon or a 3840x2160 background from pushing the metadata below the visible
        // frame. Each preview only renders when it actually decoded so a backup without one
        // reads with the list starting at the top of the column.
        var column = new StackPanel();
        var info = new ScrollMenu { ViewHeight = 500 };

        Surface? iconSurface = AsSurface(_decodedIcon);
        if (iconSurface.HasValue)
        {
            column.Add(new Image(iconSurface.Value, blend: true)
            {
                MaxWidth = 240,
                MaxHeight = 240,
            });
            column.Add(new Separator());
        }

        Surface? backgroundSurface = AsSurface(_decodedBackground);
        if (backgroundSurface.HasValue)
        {
            column.Add(new Image(backgroundSurface.Value, blend: false)
            {
                MaxWidth = 480,
                MaxHeight = 270,
                FitToWidth = true,
            });
            column.Add(new Separator());
        }

        info.Add(new KeyValueRow("Title", _backup.DisplayTitle));
        if (!string.IsNullOrEmpty(_backup.GameId))
            info.Add(new KeyValueRow("Game ID", _backup.GameId));
        if (!string.IsNullOrEmpty(_backup.ContentId))
            info.Add(new KeyValueRow("Content ID", _backup.ContentId));
        if (!string.IsNullOrEmpty(_backup.Region))
            info.Add(new KeyValueRow("Region", _backup.Region));
        if (!string.IsNullOrEmpty(_backup.Category))
            info.Add(new KeyValueRow("Category", _backup.Category));
        if (!string.IsNullOrEmpty(_backup.Version))
            info.Add(new KeyValueRow("Version", _backup.Version));
        if (!string.IsNullOrEmpty(_backup.FirmwareVersion))
            info.Add(new KeyValueRow("Firmware", _backup.FirmwareVersion));
        if (_backup.Size > 0)
            info.Add(new KeyValueRow("Size", Formatting.FormatSize(_backup.Size)));

        info.Add(new KeyValueRow("Platform", PlatformDisplayName(_backup.Platform)));
        info.Add(new KeyValueRow("Type", _backup.FileType.ToString()));

        if (!string.IsNullOrEmpty(_backup.FilePath))
            info.Add(new KeyValueRow("File", _backup.FilePath));
        if (!string.IsNullOrEmpty(_backup.FolderPath))
            info.Add(new KeyValueRow("Folder", _backup.FolderPath));

        if (_backup.Sfo is not null)
        {
            info.Add(new Separator());
            info.Add(new Label("SFO Entries") { TextColor = Shell.Theme.Accent });
            foreach (SfoEntry entry in _backup.Sfo.Entries)
            {
                string value = entry.ValueType == SfoValueType.Int32
                    ? entry.Int32Value.ToString()
                    : entry.StringValue;
                info.Add(new KeyValueRow(entry.Key, value));
            }
        }

        if (_backup.Param is not null)
        {
            info.Add(new Separator());
            info.Add(new Label("param.json") { TextColor = Shell.Theme.Accent });
            foreach (LocalizedTitle lt in _backup.Param.LocalizedTitles)
                info.Add(new KeyValueRow($"Title ({lt.Language})", lt.TitleName));
            if (!string.IsNullOrEmpty(_backup.Param.ApplicationDrmType))
                info.Add(new KeyValueRow("DRM Type", _backup.Param.ApplicationDrmType));
            if (!string.IsNullOrEmpty(_backup.Param.CreationDate))
                info.Add(new KeyValueRow("Created", _backup.Param.CreationDate));
        }

        info.Add(new Separator());

        LaunchCapability capability = _backup.GetLaunchCapability();
        switch (capability)
        {
            case LaunchCapability.Native:
                info.Add(new Button("Launch from folder", () => LaunchFolderApp()));
                break;
            case LaunchCapability.Installable:
                info.Add(new Button("Install package", () => InstallPackage()));
                if (!string.IsNullOrEmpty(_backup.GameId))
                    info.Add(new Button("Launch title", () => LaunchTitle()));
                break;
            case LaunchCapability.Emulatable:
                BuildEmulatorLaunchRows(info);
                break;
            case LaunchCapability.InspectOnly:
                info.Add(new Label(_backup.GetLaunchExplanation())
                    { TextColor = Shell.Theme.TextMuted });
                break;
        }

        if (_backup.Sfo is not null)
            info.Add(new Button("Edit SFO", () => Shell.Push(new SfoEditScreen(Shell, _backup))));

        if (_backup.Param is not null)
            info.Add(new Button("Edit param.json", () => Shell.Push(new ParamEditScreen(Shell, _backup))));

        info.Add(new Button("Copy backup", () => CopyBackup()));
        info.Add(new Button("Move backup", () => MoveBackup()));

        if (Shell.Settings.ConfirmDestructive)
        {
            info.Add(new Button("Delete backup", () =>
            {
                Shell.Dialogs.Confirm($"Delete '{_backup.DisplayTitle}'?", yes =>
                {
                    if (yes)
                        DeleteBackup();
                });
            }));
        }
        else
        {
            info.Add(new Button("Delete backup", () => DeleteBackup()));
        }

        column.Add(info);
        return column;
    }

    public override void OnShown()
    {
        // The image decoders depend on their sysmodules being loaded in MultiToolsApp.OnLoad. The
        // cover-service call may reach the network for a PS1 / PS2 / PS3 backup that has no local
        // sidecar, so it goes off the frame thread; the frame the answer comes back on rebuilds the
        // screen to draw it.
        if (!_iconLoaded)
        {
            _iconLoaded = true;
            _ = TryLoadIconAsync();
        }

        // Do not restart soundtrack when the user explicitly stopped it with Triangle.
        if (!_soundtrackPlaying && !_soundtrackUserStopped
            && Shell.Settings.PlaySoundtrack && !string.IsNullOrEmpty(_backup.SoundtrackPath))
        {
            TryPlaySoundtrack();
        }
    }

    public override void Tick(FrameContext context)
    {
        // Triangle toggles soundtrack playback as documented in the Hint text.
        if (context.Pressed(ScePadButton.Triangle))
        {
            if (_soundtrackPlaying)
            {
                StopSoundtrack();
                _soundtrackUserStopped = true;
            }
            else if (!string.IsNullOrEmpty(_backup.SoundtrackPath))
            {
                _soundtrackUserStopped = false;
                TryPlaySoundtrack();
            }
        }

        // Pull decoded audio from the media player and push it into the audio output.
        // MediaPlayer.Open depends on SystemModuleId.AvPlayer loaded in MultiToolsApp.OnLoad.
        if (_soundtrackPlaying && _player is not null && _audio is not null)
        {
            if (!_player.IsActive)
            {
                StopSoundtrack();
                return;
            }

            while (_audio.FreeBlocks > 0)
            {
                if (!_player.TryGetAudioFrame(out AudioFrame frame))
                    break;
                _audio.TryOutput(frame.Samples);
            }
        }
    }

    protected override bool OnCancel()
    {
        StopSoundtrack();
        return false;
    }

    protected override void OnDispose()
    {
        // StopSoundtrack is guarded so the image disposals below always run.
        try { StopSoundtrack(); }
        catch { /* Disposal failures must not leak the icon or background surface. */ }
        _decodedIcon?.Dispose();
        _decodedIcon = null;
        _decodedBackground?.Dispose();
        _decodedBackground = null;
    }

    private void BuildEmulatorLaunchRows(ScrollMenu menu)
    {
        EmulatorKind kind = _backup.Platform switch
        {
            GamePlatform.PS2 => EmulatorKind.PS2,
            GamePlatform.PS1 => EmulatorKind.PS1,
            GamePlatform.PSP => EmulatorKind.PSP,
            _ => EmulatorKind.PS2,
        };

        List<EmulatorDescriptor> emulators = EmulatorRegistry.GetByKind(kind);

        if (emulators.Count == 0)
        {
            menu.Add(new Label("No emulator found. Copy the emulator files to a USB drive first.")
                { TextColor = Shell.Theme.TextMuted });
            return;
        }

        // For PS2 with multiple emulator choices, add a stepper to pick one.
        if (kind == EmulatorKind.PS2 && emulators.Count > 1)
        {
            menu.Add(new Stepper("Emulator", _selectedPs2EmuIndex, 0, emulators.Count - 1,
                format: v => emulators[(int)v].Name,
                changed: v => _selectedPs2EmuIndex = (int)v));
        }

        menu.Add(new Button("Configure and launch", () =>
        {
            int emuIndex = kind == EmulatorKind.PS2 && emulators.Count > 1
                ? _selectedPs2EmuIndex
                : 0;

            if (emuIndex < 0 || emuIndex >= emulators.Count)
                emuIndex = 0;

            EmulatorDescriptor emu = emulators[emuIndex];

            IEmuOptions options = kind switch
            {
                EmulatorKind.PS2 => new Ps2EmuOptions(),
                EmulatorKind.PS1 => new Ps1EmuOptions(),
                EmulatorKind.PSP => new PspEmuOptions(),
                _ => new Ps2EmuOptions(),
            };

            Shell.Push(new EmulatorLaunchScreen(Shell, _backup, emu, options));
        }));
    }

    private async Task TryLoadIconAsync()
    {
        try
        {
            // Move the cover resolution onto the same background task the decode runs in, so a
            // cache miss that fires a synchronous HTTP GET does not stall the UI thread while
            // the network answers.
            BackupInfo backup = _backup;
            Task<IDisposable?> iconTask = Task.Run<IDisposable?>(() =>
            {
                string path = CoverService.Resolve(backup);
                return LoadImageFromPath(path);
            });
            Task<IDisposable?> backgroundTask = LoadAssetAsync(_backup.BackgroundPath);

            await Task.WhenAll(iconTask, backgroundTask).ConfigureAwait(false);

            IDisposable? icon = iconTask.Result;
            IDisposable? background = backgroundTask.Result;

            if (IsDisposed)
            {
                icon?.Dispose();
                background?.Dispose();
                return;
            }

            bool changed = false;
            if (icon is not null)
            {
                _decodedIcon = icon;
                changed = true;
            }
            if (background is not null)
            {
                _decodedBackground = background;
                changed = true;
            }
            if (changed)
                RebuildScreen();
        }
        catch (Exception e)
        {
            Shell.ReportFailure("Loading icon", e);
        }
    }

    // Decodes one image asset off the frame thread, choosing the right decoder from the extension.
    // Returns null when the path is empty, missing, or in a form the decoders do not recognise.
    private static Task<IDisposable?> LoadAssetAsync(string path)
    {
        if (string.IsNullOrEmpty(path))
            return Task.FromResult<IDisposable?>(null);

        return Task.Run<IDisposable?>(() => LoadImageFromPath(path));
    }

    // Reads image bytes from a path, routing through the broker when the path lies on a
    // partition the module cannot bind directly. Chooses the decoder from the extension and
    // returns null when the path is empty, unreadable, or in an unrecognised form.
    private static IDisposable? LoadImageFromPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        try
        {
            byte[]? bytes = ReadImageBytes(path);
            if (bytes is null || bytes.Length == 0)
                return null;

            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                return PngImage.Decode(bytes);
            if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                return JpegImage.Decode(bytes);
            return PngImage.Decode(bytes);
        }
        catch (ProsperoException) { return null; }
        catch (Exception) { return null; }
    }

    private static byte[]? ReadImageBytes(string path)
    {
        if (FileSystem.Exists(path))
        {
            try { return FileSystem.ReadAllBytes(path); }
            catch (ProsperoException) { }
        }
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
        {
            var (outcome, bytes) = SandboxBroker.ReadAllBytes(path);
            if (outcome == BrokerOutcome.Ok && bytes.Length > 0)
                return bytes;
        }
        return null;
    }

    private static Surface? AsSurface(IDisposable? image)
    {
        return image switch
        {
            PngImage png => png.AsSurface(),
            JpegImage jpeg => jpeg.AsSurface(),
            _ => null,
        };
    }

    // Where the file picker opens for a copy or a move. The first attached USB drive is the usual
    // destination; when none is attached, the picker opens on the root list so the user still has
    // somewhere to go.
    private static string PickerStartingPath()
    {
        System.Collections.Generic.IReadOnlyList<string> usbs = Places.UsbPaths();
        if (usbs.Count > 0)
            return usbs[0];
        foreach (PlaceProbe probe in Places.Reachable)
        {
            if (probe.Place.Writable)
                return probe.Place.Path;
        }
        return string.Empty;
    }

    private void TryPlaySoundtrack()
    {
        try
        {
            bool soundtrackExists = FileSystem.Exists(_backup.SoundtrackPath);
            if (!soundtrackExists
                && SandboxBroker.IsOnBrokerPartition(_backup.SoundtrackPath)
                && SandboxBroker.IsReachable())
            {
                soundtrackExists = SandboxBroker.FileExists(_backup.SoundtrackPath);
            }
            if (!soundtrackExists)
                return;

            // The media player accepts container-based files only (MP4-family, WebM). Raw AT3
            // and AT9 audio streams do not carry an MP4 wrapper; passing them to sceAvPlayer
            // answers with 0x806A0004 (invalid source) because the demuxer does not recognise
            // the header. Skip those extensions silently so a scan that lists snd0.at9 as the
            // backup's soundtrack does not show the user a repeating error dialog.
            string extension = GetSoundtrackExtension(_backup.SoundtrackPath);
            if (extension is ".at3" or ".at9")
                return;

            var audio = AudioQueueDevice.OpenStereo(512, 48000);
            try
            {
                var player = MediaPlayer.Open(_backup.SoundtrackPath);
                player.SetLooping(true);
                player.Start();

                _audio = audio;
                _player = player;
                _audio.Gain = Shell.Settings.Volume / 100f;
                _soundtrackPlaying = true;
            }
            catch
            {
                // If the player fails to open or start, dispose the audio device
                // that was already opened so it does not leak.
                audio.Dispose();
                throw;
            }
        }
        catch (Exception e)
        {
            Shell.ReportFailure("Playing soundtrack", e);
            _soundtrackPlaying = false;
        }
    }

    private static string GetSoundtrackExtension(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "";
        int dot = path.LastIndexOf('.');
        if (dot < 0)
            return "";
        string ext = path[dot..].ToLowerInvariant();
        return ext.Length <= 5 ? ext : "";
    }

    private void StopSoundtrack()
    {
        _soundtrackPlaying = false;

        // Dispose each resource independently so a failure in one does not leak the other.
        try { _player?.Dispose(); }
        catch { /* Swallow disposal failures. */ }
        _player = null;

        try { _audio?.Dispose(); }
        catch { /* Swallow disposal failures. */ }
        _audio = null;
    }

    private void InstallPackage()
    {
        if (string.IsNullOrEmpty(_backup.FilePath))
        {
            Shell.Notify("No package file to install.");
            return;
        }

        string gameId = _backup.GameId;
        if (string.IsNullOrEmpty(gameId))
        {
            Shell.Notify("No title ID for installation.");
            return;
        }

        Shell.Dialogs.Confirm($"Install '{_backup.DisplayTitle}'?", yes =>
        {
            if (!yes)
                return;

            PackageInstaller? installer = null;
            bool installStarted = false;
            bool success = false;

            Shell.Dialogs.RunWithProgress("Installing...", dialog =>
            {
                try
                {
                    if (!installStarted)
                    {
                        installer = PackageInstaller.Open();

                        if (installer.AppExists(gameId))
                            installer.PrepareOverwrite(gameId);

                        installer.Install(_backup.FilePath);
                        installStarted = true;
                        dialog.SetProgress(0);
                        dialog.SetProgressMessage($"Installing '{_backup.DisplayTitle}'...");
                        return false;
                    }

                    int progress = installer!.GetInstallProgress(gameId);
                    if (progress < 0)
                    {
                        Shell.ReportFailure("Install",
                            new ProsperoException("sceAppInstUtilGetInstallProgress", progress), true);
                        installer.Dispose();
                        installer = null;
                        return true;
                    }

                    dialog.SetProgress(progress);
                    dialog.SetProgressMessage($"Installing... {progress}%");

                    if (progress >= 100)
                    {
                        success = true;
                        installer.Dispose();
                        installer = null;
                        return true;
                    }

                    return false;
                }
                catch (Exception e)
                {
                    Shell.ReportFailure("Install", e, true);
                    installer?.Dispose();
                    installer = null;
                    return true;
                }
            }, () =>
            {
                if (success)
                {
                    Shell.Dialogs.Confirm($"Installation complete. Launch '{gameId}'?", launchYes =>
                    {
                        if (launchYes)
                            LaunchTitle();
                    });
                }
            });
        });
    }

    private void LaunchTitle()
    {
        if (string.IsNullOrEmpty(_backup.GameId))
        {
            Shell.Notify("No title ID to launch.");
            return;
        }

        Shell.Dialogs.Confirm($"Launch '{_backup.GameId}'?", yes =>
        {
            if (!yes)
                return;
            try
            {
                // AppLauncher.Launch replaces the running application with the
                // launched one. On success this call does not return.
                AppLauncher.Launch(_backup.GameId);
            }
            catch (Exception e)
            {
                Shell.ReportFailure("Launch", e, true);
            }
        });
    }

    private void LaunchFolderApp()
    {
        if (!ValidateStagingTree())
            return;

        LaunchTitle();
    }

    private bool ValidateStagingTree()
    {
        if (string.IsNullOrEmpty(_backup.FolderPath))
        {
            Shell.Notify("No folder path set for this backup.");
            return false;
        }

        string paramJson = _backup.FolderPath + "/sce_sys/param.json";
        bool paramExists = FileSystem.Exists(paramJson);
        if (!paramExists
            && SandboxBroker.IsOnBrokerPartition(paramJson)
            && SandboxBroker.IsReachable())
        {
            paramExists = SandboxBroker.FileExists(paramJson);
        }
        if (!paramExists)
        {
            Shell.Notify("Missing sce_sys/param.json in staging tree.");
            return false;
        }

        string eboot = _backup.FolderPath + "/eboot.bin";
        bool ebootExists = FileSystem.Exists(eboot);
        if (!ebootExists
            && SandboxBroker.IsOnBrokerPartition(eboot)
            && SandboxBroker.IsReachable())
        {
            ebootExists = SandboxBroker.FileExists(eboot);
        }
        if (!ebootExists)
        {
            Shell.Notify("Missing eboot.bin in staging tree.");
            return false;
        }

        string sceModule = _backup.FolderPath + "/sce_module";
        bool sceModuleIsDir = FileSystem.IsDirectory(sceModule);
        if (!sceModuleIsDir
            && SandboxBroker.IsOnBrokerPartition(sceModule)
            && SandboxBroker.IsReachable())
        {
            sceModuleIsDir = SandboxBroker.IsDirectory(sceModule);
        }
        if (!sceModuleIsDir)
        {
            Shell.Notify("Missing sce_module directory in staging tree.");
            return false;
        }

        return true;
    }

    private void CopyBackup()
    {
        FilePickerScreen.PickFolder(Shell, "Pick a destination folder", PickerStartingPath(), dest =>
        {
            if (string.IsNullOrEmpty(dest))
                return;

            string source = !string.IsNullOrEmpty(_backup.FolderPath) ? _backup.FolderPath : _backup.FilePath;
            if (string.IsNullOrEmpty(source))
            {
                Shell.Notify("Nothing to copy.");
                return;
            }

            bool success = false;
            List<string>? files = null;
            int fileIndex = 0;
            string sourceRoot = source.TrimEnd('/');
            string destRoot = dest.TrimEnd('/');

            Shell.Dialogs.RunWithProgress("Copying...", dialog =>
            {
                try
                {
                    if (files is null)
                    {
                        if (IsDirectoryWithBroker(source))
                        {
                            files = EnumerateRecursiveWithBroker(source);
                            if (files.Count == 0)
                            {
                                CreateDirectoryRecursiveWithBroker(dest);
                                success = true;
                                return true;
                            }
                            dialog.SetProgress(0);
                            dialog.SetProgressMessage($"Found {files.Count} files...");
                            return false;
                        }

                        CopyFileWithBroker(source, dest);
                        success = true;
                        return true;
                    }

                    string sourceFile = files[fileIndex];
                    string relativePath = sourceFile.Substring(sourceRoot.Length);
                    string destFile = destRoot + relativePath;

                    int lastSlash = destFile.LastIndexOf('/');
                    if (lastSlash > 0)
                    {
                        string parentDir = destFile[..lastSlash];
                        if (!ExistsWithBroker(parentDir))
                            CreateDirectoryRecursiveWithBroker(parentDir);
                    }

                    CopyFileWithBroker(sourceFile, destFile);
                    fileIndex++;

                    int progress = fileIndex * 100 / files.Count;
                    dialog.SetProgress(progress);
                    dialog.SetProgressMessage($"Copying ({fileIndex}/{files.Count})...");

                    if (fileIndex >= files.Count)
                    {
                        success = true;
                        return true;
                    }
                    return false;
                }
                catch (Exception e)
                {
                    Shell.ReportFailure("Copy", e, true);
                    return true;
                }
            }, () =>
            {
                if (success)
                    Shell.Notify("Copy complete.");
            });
        });
    }

    private void MoveBackup()
    {
        FilePickerScreen.PickFolder(Shell, "Pick a destination folder", PickerStartingPath(), dest =>
        {
            if (string.IsNullOrEmpty(dest))
                return;

            string source = !string.IsNullOrEmpty(_backup.FolderPath) ? _backup.FolderPath : _backup.FilePath;
            if (string.IsNullOrEmpty(source))
            {
                Shell.Notify("Nothing to move.");
                return;
            }

            bool success = false;
            bool atomicTried = false;
            List<string>? files = null;
            List<string>? dirs = null;
            int stepIndex = 0;
            string sourceRoot = source.TrimEnd('/');
            string destRoot = dest.TrimEnd('/');

            Shell.Dialogs.RunWithProgress("Moving...", dialog =>
            {
                try
                {
                    // An atomic rename finishes in one step when source and destination
                    // share the same file system.
                    if (!atomicTried)
                    {
                        atomicTried = true;
                        try
                        {
                            FileSystem.Move(source, dest);
                            success = true;
                            return true;
                        }
                        catch
                        {
                            // Cross-device or sandbox restriction: try broker rename
                            // before falling through to incremental copy and delete.
                            if ((SandboxBroker.IsOnBrokerPartition(source)
                                || SandboxBroker.IsOnBrokerPartition(dest))
                                && SandboxBroker.IsReachable())
                            {
                                if (SandboxBroker.Rename(source, dest) == BrokerOutcome.Ok)
                                {
                                    success = true;
                                    return true;
                                }
                            }
                        }
                    }

                    if (files is null)
                    {
                        if (IsDirectoryWithBroker(source))
                        {
                            files = EnumerateRecursiveWithBroker(source);
                            dirs = CollectDirectories(source, files);
                            if (files.Count == 0)
                            {
                                CreateDirectoryRecursiveWithBroker(dest);
                                foreach (string dir in dirs)
                                    DeleteDirectoryWithBroker(dir);
                                success = true;
                                return true;
                            }
                            dialog.SetProgress(0);
                            dialog.SetProgressMessage($"Found {files.Count} files...");
                            return false;
                        }

                        CopyFileWithBroker(source, dest);
                        DeleteFileWithBroker(source);
                        success = true;
                        return true;
                    }

                    // Three phases: copy each file, delete each source file, remove source directories.
                    int totalSteps = files.Count * 2 + dirs!.Count;

                    if (stepIndex < files.Count)
                    {
                        string sourceFile = files[stepIndex];
                        string relativePath = sourceFile.Substring(sourceRoot.Length);
                        string destFile = destRoot + relativePath;

                        int lastSlash = destFile.LastIndexOf('/');
                        if (lastSlash > 0)
                        {
                            string parentDir = destFile[..lastSlash];
                            if (!ExistsWithBroker(parentDir))
                                CreateDirectoryRecursiveWithBroker(parentDir);
                        }

                        CopyFileWithBroker(sourceFile, destFile);
                    }
                    else if (stepIndex < files.Count * 2)
                    {
                        int deleteIndex = stepIndex - files.Count;
                        DeleteFileWithBroker(files[deleteIndex]);
                    }
                    else
                    {
                        int dirIndex = stepIndex - files.Count * 2;
                        DeleteDirectoryWithBroker(dirs![dirIndex]);
                    }

                    stepIndex++;
                    int progress = stepIndex * 100 / totalSteps;
                    dialog.SetProgress(progress);
                    dialog.SetProgressMessage($"Moving ({stepIndex}/{totalSteps})...");

                    if (stepIndex >= totalSteps)
                    {
                        success = true;
                        return true;
                    }
                    return false;
                }
                catch (Exception e)
                {
                    Shell.ReportFailure("Move", e, true);
                    return true;
                }
            }, () =>
            {
                if (success)
                {
                    Shell.Notify("Move complete.");
                    Shell.Pop();
                }
            });
        });
    }

    private void DeleteBackup()
    {
        string target = !string.IsNullOrEmpty(_backup.FolderPath) ? _backup.FolderPath : _backup.FilePath;
        if (string.IsNullOrEmpty(target))
        {
            Shell.Notify("Nothing to delete.");
            return;
        }

        bool success = false;
        List<string>? files = null;
        List<string>? dirs = null;
        int stepIndex = 0;

        Shell.Dialogs.RunWithProgress("Deleting...", dialog =>
        {
            try
            {
                if (files is null)
                {
                    if (IsDirectoryWithBroker(target))
                    {
                        files = EnumerateRecursiveWithBroker(target);
                        dirs = CollectDirectories(target, files);

                        int total = files.Count + dirs.Count;
                        if (total == 0)
                        {
                            DeleteDirectoryWithBroker(target);
                            success = true;
                            return true;
                        }
                        if (files.Count == 0)
                        {
                            foreach (string dir in dirs)
                                DeleteDirectoryWithBroker(dir);
                            success = true;
                            return true;
                        }
                        dialog.SetProgress(0);
                        dialog.SetProgressMessage($"Found {total} items...");
                        return false;
                    }

                    DeleteFileWithBroker(target);
                    success = true;
                    return true;
                }

                int totalSteps = files.Count + dirs!.Count;

                if (stepIndex < files.Count)
                {
                    DeleteFileWithBroker(files[stepIndex]);
                }
                else
                {
                    int dirIndex = stepIndex - files.Count;
                    DeleteDirectoryWithBroker(dirs![dirIndex]);
                }

                stepIndex++;
                int progress = stepIndex * 100 / totalSteps;
                dialog.SetProgress(progress);
                dialog.SetProgressMessage($"Deleting ({stepIndex}/{totalSteps})...");

                if (stepIndex >= totalSteps)
                {
                    success = true;
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                Shell.ReportFailure("Delete", e, true);
                return true;
            }
        }, () =>
        {
            if (success)
            {
                Shell.Notify("Deleted.");
                Shell.Pop();
            }
        });
    }

    /// <summary>
    /// Builds the list of directories that contain files inside a tree, sorted deepest-first
    /// so each directory is empty by the time deletion reaches it. Includes the root.
    /// </summary>
    private static List<string> CollectDirectories(string root, List<string> files)
    {
        string normalizedRoot = root.TrimEnd('/');
        var seen = new Dictionary<string, bool>();
        foreach (string file in files)
        {
            string dir = file;
            while (true)
            {
                int slash = dir.LastIndexOf('/');
                if (slash < 0)
                    break;
                dir = dir[..slash];
                if (dir.Length < normalizedRoot.Length)
                    break;
                if (seen.ContainsKey(dir))
                    break;
                seen[dir] = true;
            }
        }
        seen[normalizedRoot] = true;
        var result = new List<string>(seen.Keys);
        result.Sort((a, b) => b.Length.CompareTo(a.Length));
        return result;
    }

    // ---- Broker-fallback wrappers ----
    //
    // Each wrapper tries the direct FileSystem call first. On failure (false return or
    // ProsperoException), when the path lies on a broker partition and the daemon is
    // reachable, the wrapper retries through SandboxBroker. This keeps /user and /data
    // accessible while /user2 and /hdd stay direct-only.

    private static bool IsDirectoryWithBroker(string path)
    {
        if (FileSystem.IsDirectory(path))
            return true;
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            return SandboxBroker.IsDirectory(path);
        return false;
    }

    private static bool ExistsWithBroker(string path)
    {
        if (FileSystem.Exists(path))
            return true;
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            return SandboxBroker.FileExists(path);
        return false;
    }

    private static List<string> EnumerateRecursiveWithBroker(string path)
    {
        try
        {
            return FileSystem.EnumerateRecursive(path);
        }
        catch (ProsperoException)
        {
            if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
                return BrokerEnumerateRecursive(path);
            throw;
        }
    }

    private static List<string> BrokerEnumerateRecursive(string root)
    {
        var result = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root.TrimEnd('/'));
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            var (outcome, entries) = SandboxBroker.List(dir);
            if (outcome != BrokerOutcome.Ok)
                continue;
            foreach (BrokerDirEntry entry in entries)
            {
                string full = dir + "/" + entry.Name;
                if (entry.IsDirectory)
                    stack.Push(full);
                else if (entry.IsFile)
                    result.Add(full);
            }
        }
        return result;
    }

    private static void CreateDirectoryRecursiveWithBroker(string path)
    {
        try
        {
            FileSystem.CreateDirectoryRecursive(path);
        }
        catch (ProsperoException)
        {
            if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            {
                if (SandboxBroker.MkdirRecursive(path) == BrokerOutcome.Ok)
                    return;
            }
            throw;
        }
    }

    private static void CopyFileWithBroker(string source, string dest)
    {
        try
        {
            FileSystem.CopyFile(source, dest);
        }
        catch (ProsperoException)
        {
            bool canBroker = (SandboxBroker.IsOnBrokerPartition(source)
                || SandboxBroker.IsOnBrokerPartition(dest))
                && SandboxBroker.IsReachable();
            if (!canBroker)
                throw;

            byte[] bytes;
            if (SandboxBroker.IsOnBrokerPartition(source))
            {
                var (readOutcome, data) = SandboxBroker.ReadAllBytes(source);
                if (readOutcome != BrokerOutcome.Ok)
                    throw;
                bytes = data;
            }
            else
            {
                bytes = FileSystem.ReadAllBytes(source);
            }

            if (SandboxBroker.IsOnBrokerPartition(dest))
            {
                if (SandboxBroker.WriteAllBytes(dest, bytes) != BrokerOutcome.Ok)
                    throw;
            }
            else
            {
                FileSystem.WriteAllBytes(dest, bytes);
            }
        }
    }

    private static void DeleteFileWithBroker(string path)
    {
        try
        {
            FileSystem.DeleteFile(path);
        }
        catch (ProsperoException)
        {
            if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            {
                if (SandboxBroker.Unlink(path) == BrokerOutcome.Ok)
                    return;
            }
            throw;
        }
    }

    private static void DeleteDirectoryWithBroker(string path)
    {
        try
        {
            FileSystem.DeleteDirectory(path);
        }
        catch (ProsperoException)
        {
            if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            {
                if (SandboxBroker.Unlink(path) == BrokerOutcome.Ok)
                    return;
            }
            throw;
        }
    }

    private static string PlatformDisplayName(GamePlatform p) => p switch
    {
        GamePlatform.PS1 => "PlayStation 1",
        GamePlatform.PS2 => "PlayStation 2",
        GamePlatform.PS3 => "PlayStation 3",
        GamePlatform.PS4 => "PlayStation 4",
        GamePlatform.PS5 => "PlayStation 5",
        GamePlatform.PSP => "PlayStation Portable",
        GamePlatform.PSVita => "PlayStation Vita",
        _ => "Unknown",
    };
}
