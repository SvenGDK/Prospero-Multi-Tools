// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Data;
using ProsperoMultiTools.Shell;
using SharpProspero.Application;
using SharpProspero.Graphics;
using SharpProspero.Interop;
using SharpProspero.Platform;
using SharpProspero.Storage;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ProsperoMultiTools.Browser;

internal sealed class BackupBrowserScreen : MultiToolsScreen
{
    private readonly GamePlatform _platform;
    private readonly string? _initialFolder;
    private readonly ListView _list;
    private readonly TextBlock _summary;
    private readonly TextBlock _previewInfo;
    private readonly Image _previewImage;
    private readonly Separator _previewSeparator;
    private readonly Button _cancelButton;
    private readonly List<BackupInfo> _backups = [];
    private readonly List<string> _rows = [];
    private readonly Dictionary<string, IDisposable?> _iconCache = new(StringComparer.Ordinal);
    private IDisposable? _previewIcon;
    private int _previewIndex = -1;
    private Task<(string Key, IDisposable? Image)>? _pendingIcon;
    private int _pendingIconIndex = -1;
    private bool _scanning;
    private bool _scanDone;
    private int _scanIndex;
    private int _lastSyncedCount;
    private List<string> _searchPaths = [];
    private Task<List<BackupInfo>>? _pendingScan;
    private CancellationTokenSource? _scanCts;
    private string? _singleScanFolder;

    public BackupBrowserScreen(MultiToolsShell shell, GamePlatform platform, string? initialFolder = null) : base(shell)
    {
        _platform = platform;
        _initialFolder = initialFolder;
        _list = new ListView { VisibleRows = shell.Settings.ListRows };
        _list.Activated = OnItemActivated;
        _list.SelectionChanged = OnSelectionChanged;
        _summary = new TextBlock { TextColor = shell.Theme.TextMuted };
        _previewInfo = new TextBlock { TextColor = shell.Theme.Text };
        _cancelButton = new Button("Cancel scan", CancelScan) { Visible = false };
        // A zero-sized default Surface holds the placeholder; Image.Measure returns 0 and Surface's
        // Blit / BlitBlended both early-return when the source has no pixels, so the control draws
        // nothing while it is invisible and stays safe to keep in the tree from BuildRoot.
        //
        // A cover the shell reads back from a PS5 backup's own icon0.png is 512x512 pixels. Left
        // uncapped, the Image control would draw at the source's own size and take half the screen.
        // Capping the maximum to a 240x240 tile fits the right-hand details panel at every list
        // row height and preserves the icon's aspect ratio.
        _previewImage = new Image(default, blend: true)
        {
            Visible = false,
            MaxWidth = 200,
            MaxHeight = 200,
        };
        _previewSeparator = new Separator { Visible = false };
    }

    public override string Title => $"{PlatformName(_platform)} Backups";

    public override string Hint => "Cross opens, L1/R1 page, L2/R2 jump, Circle goes back.";

    protected override UiElement BuildRoot()
    {
        _summary.Text = _scanDone
            ? (_backups.Count == 0
                ? "No backups found. Connect a USB drive or use Browse folder."
                : $"{_backups.Count} backup{(_backups.Count == 1 ? "" : "s")} found.")
            : "Scanning...";

        SyncListView();
        RefreshPreview(_list.SelectedIndex);

        // Left column: the list of scanned backups and the actions below it. The list scrolls
        // through every row so a long collection still reaches the last row without paging.
        var leftColumn = new StackPanel();
        leftColumn.Add(_list);
        leftColumn.Add(new Separator());
        leftColumn.Add(new Button("Scan again", StartScan));
        leftColumn.Add(new Button("Browse folder...", () =>
        {
            string start = Places.StartingPoint(Shell.Settings.StartPath) ?? "";
            FilePickerScreen.PickFolder(Shell, "Pick a folder to scan", start, path =>
            {
                if (!string.IsNullOrWhiteSpace(path))
                    ScanSingleFolder(path);
            });
        }));
        leftColumn.Add(_cancelButton);

        // Right column: the focused row's cover and details. Both are preserved instances so
        // BuildRoot runs once for the life of the screen; a re-focus mutates them in place and
        // the actions on the left keep their identity.
        var rightColumn = new StackPanel();
        rightColumn.Add(_previewImage);
        rightColumn.Add(_previewSeparator);
        rightColumn.Add(_previewInfo);

        // A weighted split lays the two columns side by side. The list gets three shares of the
        // width and the details gets two, so a long backup name reads without truncation and the
        // preview panel still fits an icon tile at 1920x1080. The summary sits above the split so
        // the total and the scanning state read without the split pulling the eye left or right.
        var split = new SplitPanel { Spacing = 12 };
        split.Add(leftColumn, weight: 3);
        split.Add(rightColumn, weight: 2);

        return new StackPanel()
            .Add(_summary)
            .Add(new Separator())
            .Add(split);
    }

    public override void OnShown()
    {
        if (!_scanDone)
        {
            if (_initialFolder is not null)
                ScanSingleFolder(_initialFolder);
            else
                StartScan();
        }
    }

    public override void Tick(FrameContext context)
    {
        PumpPendingIcon();

        if (!_scanning)
            return;

        try
        {
            if (_pendingScan is not null)
            {
                if (!_pendingScan.IsCompleted)
                    return;

                if (_pendingScan.IsCompletedSuccessfully)
                {
                    // On a fresh scan the list starts empty and initial focus lands on the "Scan again"
                    // button (the ListView is not focusable until it holds rows). Note the pre-add state
                    // here so the "populated for the first time" transition can route focus back onto
                    // the list; a later top-up (a second scan path adding rows) leaves focus alone so a
                    // user who has already arrowed to a button is not yanked back. Restoring focus is
                    // done here from Tick (never from SyncListView, which BuildRoot also calls before
                    // the Screen field is set) so the Screen getter cannot recurse.
                    bool wasListEmpty = _list.Items.Count == 0;
                    List<BackupInfo> found = _pendingScan.Result;
                    foreach (BackupInfo info in found)
                    {
                        _backups.Add(info);
                        _rows.Add(FormatEntry(info));
                    }
                    SyncListView();
                    if (wasListEmpty && _list.Items.Count > 0)
                    {
                        try { Screen.RestoreFocus(_list); }
                        catch { }
                    }
                }
                else if (_pendingScan.IsFaulted)
                {
                    Exception ex = _pendingScan.Exception?.InnerException ?? _pendingScan.Exception!;
                    Shell.ReportFailure("Scan", ex);
                }

                _pendingScan = null;
            }

            if (_scanIndex < _searchPaths.Count)
            {
                string path = _searchPaths[_scanIndex];
                _scanIndex++;

                CancellationToken token = _scanCts?.Token ?? CancellationToken.None;
                GamePlatform platform = _platform;
                _pendingScan = Task.Run(() => BackupScanner.ScanFolder(path, platform, cancel: token));

                _summary.Text = $"Scanning folder {_scanIndex} of {_searchPaths.Count}... {_backups.Count} found so far.";
            }
            else
            {
                FinishScan();
            }
        }
        catch (Exception e)
        {
            Shell.ReportFailure("Scan", e);
            FinishScan();
        }
    }

    private void StartScan()
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();

        _backups.Clear();
        _previewIndex = -1;
        _previewIcon = null;
        _previewInfo.Text = string.Empty;
        UpdatePreviewVisibility();
        _rows.Clear();
        _list.Clear();
        _lastSyncedCount = 0;
        _scanIndex = 0;
        _scanDone = false;
        _scanning = true;
        _pendingScan = null;
        _singleScanFolder = null;
        _cancelButton.Visible = true;

        _searchPaths = BuildSearchPaths();
        _summary.Text = $"Scanning {_searchPaths.Count} paths...";
    }

    private void ScanSingleFolder(string path)
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();

        _backups.Clear();
        _previewIndex = -1;
        _previewIcon = null;
        _previewInfo.Text = string.Empty;
        UpdatePreviewVisibility();
        _rows.Clear();
        _list.Clear();
        _lastSyncedCount = 0;
        _scanIndex = 0;
        _scanDone = false;
        _scanning = true;
        _pendingScan = null;
        _singleScanFolder = path;
        _cancelButton.Visible = true;

        _searchPaths = [path];
        _summary.Text = $"Scanning {path}...";
    }

    private void FinishScan()
    {
        _scanning = false;
        _scanDone = true;
        _pendingScan = null;
        _cancelButton.Visible = false;

        if (_backups.Count == 0)
        {
            _summary.Text = _singleScanFolder is not null
                ? $"No backups found in {_singleScanFolder}. Try a different folder or connect a USB drive."
                : "No backups found. Connect a USB drive with your backups, or use Browse folder.";
        }
        else
        {
            _summary.Text = _singleScanFolder is not null
                ? $"{_backups.Count} backup{(_backups.Count == 1 ? "" : "s")} found in {_singleScanFolder}."
                : $"{_backups.Count} backup{(_backups.Count == 1 ? "" : "s")} found.";
        }
        _singleScanFolder = null;
    }

    private void CancelScan()
    {
        _scanCts?.Cancel();
        _scanning = false;
        _scanDone = true;
        _pendingScan = null;
        _cancelButton.Visible = false;
        _singleScanFolder = null;
        _summary.Text = $"{_backups.Count} backups found (scan stopped).";
    }

    // The paths a scan walks: any user-set list wins outright; otherwise the writable local root and
    // every attached USB drive. System partitions and read-only mounts are skipped because a backup
    // does not live there and walking them costs frames for no result.
    private List<string> BuildSearchPaths()
    {
        var paths = new List<string>();

        string custom = Shell.Settings.BackupSearchPath;
        if (!string.IsNullOrEmpty(custom))
        {
            foreach (string part in SplitPaths(custom))
            {
                if (FileSystem.TryEnumerateDirectory(part, out _, out _)
                    || (SandboxBroker.IsOnBrokerPartition(part) && SandboxBroker.IsReachable() && SandboxBroker.IsDirectory(part)))
                    paths.Add(part);
            }
            if (paths.Count > 0)
                return paths;
        }

        // /data is broker-visible after the daemon widens the sandbox root vnode. The root is
        // added when either the direct filesystem call succeeds or the broker can see it - the
        // daemon runs outside the caller's sandbox and reaches the path even when the direct
        // open would answer with EINVAL. Backups placed under /data/homebrew land here so every
        // platform tab that walks the file system reaches them. /user is deliberately left out
        // of the walk: it holds save data, appmeta rows, and shell state that the scanner would
        // otherwise pull into a per-platform tab as fake backups.
        if (FileSystem.TryEnumerateDirectory("/data", out _, out _)
            || (SandboxBroker.IsReachable() && SandboxBroker.IsDirectory("/data")))
            paths.Add("/data");

        foreach (string usb in Places.UsbPaths())
            paths.Add(usb);

        return paths;
    }

    private void SyncListView()
    {
        for (int i = _lastSyncedCount; i < _rows.Count; i++)
            _list.Add(_rows[i]);
        _lastSyncedCount = _rows.Count;
        UpdatePreviewVisibility();
        if (_previewIndex < 0 && _backups.Count > 0)
            RefreshPreview(_list.SelectedIndex);
    }

    // Toggles the preview separator and info-line visibility as one unit driven by whether the scan
    // has produced any backups. The image is toggled on its own from RefreshPreview because a
    // backup may be present without a cover having decoded yet.
    private void UpdatePreviewVisibility()
    {
        bool has = _backups.Count > 0;
        _previewSeparator.Visible = has;
        _previewInfo.Visible = has;
        if (!has)
            _previewImage.Visible = false;
    }

    private void OnItemActivated(int index)
    {
        if (index < 0 || index >= _backups.Count)
            return;
        Shell.Push(new BackupDetailScreen(Shell, _backups[index]));
    }

    private void OnSelectionChanged(int index)
    {
        // The preview panel is mutated in place: no rebuild is fired here because a rebuild would
        // discard the UiScreen (dropping Focused) and every anonymous element in BuildRoot (the
        // ScrollMenu with its scroll offset, both action Buttons). The user's next arrow-key would
        // then land on a stale reference — the visible "focus jumps" symptom this OnSelectionChanged
        // used to cause on every keypress.
        RefreshPreview(index);
    }

    // Loads the focused backup's icon (once, then from a cache) and rewrites the preview line to
    // match. The Image and TextBlock are preserved fields; a re-focus mutates their content in
    // place so the next Draw picks it up without a screen rebuild.
    private void RefreshPreview(int index)
    {
        if (index < 0 || index >= _backups.Count)
        {
            _previewIndex = -1;
            _previewIcon = null;
            _previewInfo.Text = string.Empty;
            _previewImage.Visible = false;
            return;
        }

        if (_previewIndex == index)
            return;
        _previewIndex = index;

        BackupInfo info = _backups[index];
        _previewInfo.Text = BuildPreviewSummary(info);

        // Local sidecar the scan already found: draw right away without going to the network.
        if (TryGetCachedIcon(info, out IDisposable? cached))
        {
            _previewIcon = cached;
            ApplyPreviewSurface(AsSurface(cached));
            return;
        }

        // Not on disk: clear the current preview and dispatch a background fetch the frame loop
        // will pick up on the next tick.
        _previewIcon = null;
        ApplyPreviewSurface(null);
        StartIconLookup(index, info);
    }

    // Points the preview Image at the given surface (or hides the image when there is nothing to
    // draw) without going through a screen rebuild. The Image control's SetContent lands on the
    // next Draw because the Image is a preserved field in the ScrollMenu's child list.
    private void ApplyPreviewSurface(Surface? surface)
    {
        if (surface.HasValue)
        {
            _previewImage.SetContent(surface.Value);
            _previewImage.Visible = true;
        }
        else
        {
            _previewImage.Visible = false;
        }
    }

    // Returns true when a cover for this backup is either already cached or already known to have
    // no answer (a miss). Sets <paramref name="image"/> to the cached decode, which may be null when
    // the backup itself has no icon.
    private bool TryGetCachedIcon(BackupInfo info, out IDisposable? image)
    {
        image = null;
        if (string.IsNullOrEmpty(info.IconPath))
            return false;

        lock (_iconCache)
        {
            if (_iconCache.TryGetValue(info.IconPath, out IDisposable? sidecar))
            {
                image = sidecar;
                return true;
            }
        }
        IDisposable? decoded = DecodeLocalCover(info.IconPath);
        lock (_iconCache)
        {
            if (_iconCache.TryGetValue(info.IconPath, out IDisposable? raced))
            {
                decoded?.Dispose();
                image = raced;
                return true;
            }
            _iconCache[info.IconPath] = decoded;
        }
        image = decoded;
        return true;
    }

    // Dispatches the cover fetch to a background task so the frame loop keeps drawing while the
    // network answers. When the task settles, the next Tick picks up the result, adds it to the
    // cache and rebuilds the screen so the preview picks up its image on the following frame. When
    // a previous fetch is still in flight, its result is still captured into the cache via a
    // continuation so a fast navigation cannot drop a decoded image on the floor.
    private void StartIconLookup(int index, BackupInfo info)
    {
        Task<(string Key, IDisposable? Image)>? inFlight = _pendingIcon;
        if (inFlight is not null)
        {
            _ = inFlight.ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully)
                    return;
                (string key, IDisposable? image) = t.Result;
                if (string.IsNullOrEmpty(key))
                    return;
                lock (_iconCache)
                {
                    if (!_iconCache.TryAdd(key, image))
                        image?.Dispose();
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
        }

        _pendingIconIndex = index;
        _pendingIcon = Task.Run<(string, IDisposable?)>(() =>
        {
            try
            {
                string path = CoverService.Resolve(info);
                if (string.IsNullOrEmpty(path))
                    return (string.Empty, null);
                return (path, DecodeLocalCover(path));
            }
            catch
            {
                return (string.Empty, null);
            }
        });
    }

    // Decodes a cover file from local storage (a sidecar or a network cache entry). Returns null
    // when the file is missing, unreadable, or in a form the decoders do not recognise. Routes
    // through the broker when the file lies on a partition the module cannot bind directly so a
    // cover cached under /data survives the sandbox's own EINVAL for that path.
    private static IDisposable? DecodeLocalCover(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        try
        {
            byte[]? bytes = null;
            if (FileSystem.Exists(path))
            {
                try { bytes = FileSystem.ReadAllBytes(path); }
                catch (ProsperoException) { }
            }
            if (bytes is null && SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            {
                var (outcome, brokerBytes) = SandboxBroker.ReadAllBytes(path);
                if (outcome == BrokerOutcome.Ok && brokerBytes.Length > 0)
                    bytes = brokerBytes;
            }
            if (bytes is null || bytes.Length == 0)
                return null;
            return DecodeImage(path, bytes);
        }
        catch (ProsperoException) { return null; }
        catch (Exception) { return null; }
    }

    // Picks a decoder from the file extension: PNG when the name says so, JPEG otherwise. Both types
    // hand back a Surface through AsSurface, so a caller sees them the same way.
    private static IDisposable? DecodeImage(string path, byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return null;

        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return PngImage.Decode(bytes);
        if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            return JpegImage.Decode(bytes);

        // A file whose extension does not name a decoder is treated as JPEG, which is what the
        // toolkit's own library serves and what most sidecar covers use.
        return JpegImage.Decode(bytes);
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

    private static string BuildPreviewSummary(BackupInfo info)
    {
        // Each fact on its own line so the right-hand details panel reads top-to-bottom as a card
        // rather than one long horizontal string that the outline text truncates against the panel
        // width. The TextBlock the caller draws this into wraps every line and never elides.
        var sb = new System.Text.StringBuilder();
        sb.Append(info.DisplayTitle);
        if (!string.IsNullOrEmpty(info.GameId))
            sb.Append('\n').Append("Game ID: ").Append(info.GameId);
        if (!string.IsNullOrEmpty(info.ContentId))
            sb.Append('\n').Append("Content ID: ").Append(info.ContentId);
        if (!string.IsNullOrEmpty(info.Region))
            sb.Append('\n').Append("Region: ").Append(info.Region);
        if (!string.IsNullOrEmpty(info.Category))
            sb.Append('\n').Append("Category: ").Append(info.Category);
        if (!string.IsNullOrEmpty(info.Version))
            sb.Append('\n').Append("Version: ").Append(info.Version);
        if (!string.IsNullOrEmpty(info.FirmwareVersion))
            sb.Append('\n').Append("Firmware: ").Append(info.FirmwareVersion);
        if (info.Size > 0)
            sb.Append('\n').Append("Size: ").Append(Formatting.FormatSize(info.Size));
        sb.Append('\n').Append("Type: ").Append(info.FileType);
        if (!string.IsNullOrEmpty(info.SoundtrackPath))
            sb.Append('\n').Append("Soundtrack included");
        if (!string.IsNullOrEmpty(info.BackgroundPath))
            sb.Append('\n').Append("Background included");
        return sb.ToString();
    }

    // Consumes the result of the background icon fetch when it settles and reflects it in the
    // preview. The cache is written under a lock because a superseded fetch's ContinueWith
    // reaches it from the task-scheduler thread. On a re-selection of the same row, the fresh
    // decode collides with an already-cached entry; the shown image is then the cached copy
    // (the fresh decode is disposed), so the preview updates on every walk of the same row
    // instead of blanking on the second walk.
    //
    // The resolved cover path is written back into the backup's IconPath as the persistent
    // cache key. TryGetCachedIcon reads that path on the next visit and hits the cache
    // without dispatching a new lookup, which is what keeps the icon on screen while the
    // user pages up and down through the list.
    private void PumpPendingIcon()
    {
        Task<(string Key, IDisposable? Image)>? task = _pendingIcon;
        if (task is null || !task.IsCompleted)
            return;

        _pendingIcon = null;
        int settledIndex = _pendingIconIndex;
        _pendingIconIndex = -1;

        if (!task.IsCompletedSuccessfully)
            return;

        (string key, IDisposable? image) = task.Result;
        if (string.IsNullOrEmpty(key))
            return;

        // Write the resolved path back so a re-selection of this row hits the cache. This
        // side-effect stays on the tick thread (single reader/writer for _backups), so no
        // extra lock is needed for the IconPath assignment.
        if (settledIndex >= 0 && settledIndex < _backups.Count
            && string.IsNullOrEmpty(_backups[settledIndex].IconPath))
        {
            _backups[settledIndex].IconPath = key;
        }

        IDisposable? liveImage;
        lock (_iconCache)
        {
            if (_iconCache.TryGetValue(key, out IDisposable? existing))
            {
                liveImage = existing;
                if (!ReferenceEquals(existing, image))
                    image?.Dispose();
            }
            else
            {
                _iconCache[key] = image;
                liveImage = image;
            }
        }

        if (settledIndex == _previewIndex && liveImage is not null)
        {
            _previewIcon = liveImage;
            ApplyPreviewSurface(AsSurface(liveImage));
        }
    }

    protected override void OnDispose()
    {
        // Cancel every background task the screen still owns before its state goes away. A scan
        // task or a cover fetch that carries on running after Dispose reaches for _iconCache and
        // _backups after they were cleared; a Task.Run that outlives its own screen used to fault
        // the process on the way back to the home menu.
        try { _scanCts?.Cancel(); } catch { }
        try { _scanCts?.Dispose(); } catch { }
        _scanCts = null;
        _pendingScan = null;
        _pendingIcon = null;
        _pendingIconIndex = -1;
        _previewIcon = null;
        lock (_iconCache)
        {
            foreach (IDisposable? image in _iconCache.Values)
                image?.Dispose();
            _iconCache.Clear();
        }
    }

    private static string FormatEntry(BackupInfo info)
    {
        string size = info.Size > 0 ? $"  [{Formatting.FormatSize(info.Size)}]" : "";
        string id = !string.IsNullOrEmpty(info.GameId) ? $"  ({info.GameId})" : "";
        // A backup whose param.sfo carried no TITLE and no TITLE_ID would otherwise draw as an empty
        // row. Fall back to a name derived from the file or folder path so every row shows something.
        string title = info.DisplayTitle;
        if (string.IsNullOrEmpty(title))
        {
            string path = !string.IsNullOrEmpty(info.FilePath) ? info.FilePath : info.FolderPath;
            int slash = path.LastIndexOf('/');
            title = slash >= 0 ? path.Substring(slash + 1) : path;
            if (string.IsNullOrEmpty(title))
                title = "(untitled)";
        }
        return $"{title}{id}{size}";
    }

    private static string PlatformName(GamePlatform platform) => platform switch
    {
        GamePlatform.PS1 => "PS1",
        GamePlatform.PS2 => "PS2",
        GamePlatform.PS3 => "PS3",
        GamePlatform.PS4 => "PS4",
        GamePlatform.PS5 => "PS5",
        GamePlatform.PSP => "PSP",
        GamePlatform.PSVita => "PS Vita",
        _ => "Unknown",
    };

    private static string[] SplitPaths(string paths)
    {
        var result = new List<string>();
        int start = 0;
        for (int i = 0; i <= paths.Length; i++)
        {
            if (i == paths.Length || paths[i] == ';')
            {
                if (i > start)
                {
                    string part = paths.Substring(start, i - start).Trim();
                    if (part.Length > 0)
                        result.Add(part);
                }
                start = i + 1;
            }
        }
        return result.ToArray();
    }
}
