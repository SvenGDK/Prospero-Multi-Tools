// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using SharpProspero.Application;
using SharpProspero.Storage;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;

namespace ProsperoMultiTools.Shell;

/// <summary>How <see cref="FilePickerScreen"/> hands the picked path back.</summary>
internal enum PickerKind
{
    /// <summary>Select a file. Folders are only for drilling in; only file rows call back.</summary>
    File,

    /// <summary>Select a folder. A "Choose this folder" row confirms the current view.</summary>
    Folder,
}

/// <summary>How a picker is set up.</summary>
internal sealed class FilePickerOptions
{
    /// <summary>Whether the picker is for a file or a folder.</summary>
    public PickerKind Kind { get; init; } = PickerKind.File;

    /// <summary>Text at the top of the picker. Names what the caller is asking for.</summary>
    public string Title { get; init; } = "Pick";

    /// <summary>Where the picker starts. Empty starts at the list of reachable roots.</summary>
    public string StartingPath { get; init; } = "";

    /// <summary>
    /// File-mode extension whitelist (each entry includes the leading dot, lower-case, e.g. ".iso").
    /// Empty accepts every file. Ignored in folder mode.
    /// </summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];
}

/// <summary>
/// A file and folder picker built out of the shell's own controls. Opens on a chosen root, walks the
/// filesystem in place, and returns the picked path (or null when the user leaves without one) through
/// a callback. Hidden entries (a name starting with a dot) follow <see cref="AppSettings.ShowHiddenFiles"/>.
/// </summary>
internal sealed class FilePickerScreen : MultiToolsScreen
{
    private readonly FilePickerOptions _options;
    private readonly Action<string?> _picked;
    private string _current = "";
    private bool _atRoot;
    private bool _returned;

    public FilePickerScreen(MultiToolsShell shell, FilePickerOptions options, Action<string?> picked)
        : base(shell)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(picked);
        _options = options;
        _picked = picked;

        string starting = options.StartingPath;
        if (string.IsNullOrEmpty(starting) || !CanEnumerateFolder(starting))
        {
            starting = "";
        }

        if (string.IsNullOrEmpty(starting))
        {
            _atRoot = true;
        }
        else
        {
            _current = starting;
            _atRoot = false;
        }
    }

    public override string Title => _atRoot ? _options.Title : $"{_options.Title} - {_current}";

    public override string Hint => _options.Kind == PickerKind.Folder
        ? "Cross opens, Triangle chooses, L1/R1 page, L2/R2 jump, Circle goes back."
        : "Cross opens or picks, L1/R1 page, L2/R2 jump, Circle goes back.";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = Shell.Settings.ListRows * Shell.Theme.LineHeight };

        // "Type path manually" is always the first row so a user who knows the target path can jump
        // straight there without walking a folder tree. It also gives a keyboard route to any path
        // the picker's own probe missed - a mount that a caller knows exists but Places.Reachable
        // did not answer for.
        menu.Add(new Button("Type path manually...", TypePathManually));

        if (_options.Kind == PickerKind.Folder && !_atRoot)
            menu.Add(new Button("Choose this folder", () => ReturnPath(_current)));

        if (_atRoot)
            AddRootEntries(menu);
        else
            AddFolderEntries(menu);

        var panel = new StackPanel().Add(menu);
        if (!_atRoot)
            panel.Add(new Label(_current) { TextColor = Shell.Theme.TextMuted });
        return panel;
    }

    private void TypePathManually()
    {
        string initial = _current.Length > 0 ? _current : "/";
        string prompt = _options.Kind == PickerKind.File
            ? "Type a file path"
            : "Type a folder path";
        Shell.Dialogs.AskText(prompt, initial, path =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            path = path.Trim();

            // A folder-mode manual entry treats the typed path as the chosen folder; a file-mode
            // manual entry treats it as the chosen file. Both check that the target actually
            // resolves so a typo does not silently pass through to the caller.
            if (_options.Kind == PickerKind.Folder)
            {
                if (CanEnumerateFolder(path))
                    ReturnPath(path);
                else if (FileSystem.TryEnumerateDirectory(path, out _, out int listError))
                    ReturnPath(path);
                else
                    Shell.ReportFailure("Path", new Exception(
                        SharpProspero.Interop.SceResult.Describe(listError)));
                return;
            }

            // File mode: accept a file path when it exists; accept a folder path and drill in.
            if (FileSystem.Exists(path) || BrokerFileExists(path))
            {
                ReturnPath(path);
                return;
            }
            if (CanEnumerateFolder(path))
            {
                _current = path;
                _atRoot = false;
                RebuildScreen();
                return;
            }
            if (FileSystem.TryEnumerateDirectory(path, out _, out int enterError))
            {
                _current = path;
                _atRoot = false;
                RebuildScreen();
                return;
            }
            Shell.ReportFailure("Path", new Exception(
                SharpProspero.Interop.SceResult.Describe(enterError)));
        }, maxLength: 512);
    }

    protected override bool OnCancel()
    {
        if (_atRoot)
        {
            // ReturnPath already pops the picker off the stack; report handled so the shell's own
            // cancel path does not pop a second time and take the caller with it.
            ReturnPath(null);
            return true;
        }

        string parent = ParentOf(_current);
        if (string.IsNullOrEmpty(parent) || parent == _current)
        {
            _current = "";
            _atRoot = true;
        }
        else
        {
            _current = parent;
        }

        RebuildScreen();
        return true;
    }

    public override void Tick(FrameContext context)
    {
        if (_options.Kind == PickerKind.Folder && !_atRoot
            && context.Pressed(SharpProspero.Interop.Pad.ScePadButton.Triangle))
        {
            ReturnPath(_current);
        }
    }

    private void AddRootEntries(ScrollMenu menu)
    {
        bool any = false;
        foreach (PlaceProbe probe in Places.Reachable)
        {
            any = true;
            Place place = probe.Place;
            menu.Add(new Button($"{place.Path}   -   {place.Name}", () => EnterFolder(place.Path)));
        }
        if (!any)
            menu.Add(new Label("No folder can be read.") { TextColor = Shell.Theme.TextMuted });
    }

    private void AddFolderEntries(ScrollMenu menu)
    {
        IReadOnlyList<DirectoryEntry>? entries;
        bool useBroker = false;
        if (!FileSystem.TryEnumerateDirectory(_current, out entries!, out int error))
        {
            // The direct call fails with EINVAL when the path is on a partition the module's
            // mount namespace does not bind (/data, /user, ...). The escalation daemon can
            // enumerate those paths because it runs outside the sandbox; walk through the
            // broker whenever the direct enumeration refused a partition the broker knows.
            if (SandboxBroker.IsOnBrokerPartition(_current) && SandboxBroker.IsReachable())
            {
                var brokerResult = SandboxBroker.List(_current);
                if (brokerResult.Outcome == BrokerOutcome.Ok)
                {
                    var converted = new List<DirectoryEntry>(brokerResult.Entries.Count);
                    foreach (BrokerDirEntry be in brokerResult.Entries)
                    {
                        FileEntryType kind = be.IsDirectory ? FileEntryType.Directory
                            : be.IsFile ? FileEntryType.File
                            : FileEntryType.Unknown;
                        converted.Add(new DirectoryEntry { Name = be.Name, Type = kind });
                    }
                    entries = converted;
                    useBroker = true;
                }
                else
                {
                    menu.Add(new Label(
                        $"Cannot read: {SharpProspero.Interop.SceResult.Describe(error)} (broker: {brokerResult.Outcome}).")
                        { TextColor = Shell.Theme.TextMuted });
                    return;
                }
            }
            else
            {
                menu.Add(new Label($"Cannot read: {SharpProspero.Interop.SceResult.Describe(error)}")
                    { TextColor = Shell.Theme.TextMuted });
                return;
            }
        }

        // "Go up one level" is always the first navigation row so a controller-only user has an
        // obvious way to reach the parent without hunting for Circle. Root paths (path=="/") have
        // no parent to go up to.
        string parent = ParentOf(_current);
        if (!string.IsNullOrEmpty(parent) && parent != _current)
            menu.Add(new Button("..  (up one level)", () =>
            {
                _current = parent;
                _atRoot = false;
                RebuildScreen();
            }));

        var folders = new List<DirectoryEntry>();
        var files = new List<DirectoryEntry>();
        bool showHidden = Shell.Settings.ShowHiddenFiles;
        foreach (DirectoryEntry entry in entries)
        {
            if (!showHidden && entry.Name.Length > 0 && entry.Name[0] == '.')
                continue;

            bool isDir;
            if (entry.Type == FileEntryType.Directory)
            {
                isDir = true;
            }
            else if (entry.Type == FileEntryType.Unknown)
            {
                // Broker replies do not always report the kind, and neither does every direct
                // getdents-style enumerate. Ask the same source that produced the entry for the
                // kind so a listing seen through the broker also identifies its folders.
                isDir = useBroker
                    ? SandboxBroker.IsDirectory(FullPath(entry.Name))
                    : FileSystem.GetEntryType(FullPath(entry.Name)) == FileEntryType.Directory;
            }
            else
            {
                isDir = false;
            }

            if (isDir)
                folders.Add(entry);
            else if (_options.Kind == PickerKind.File && AcceptsFile(entry.Name))
                files.Add(entry);
        }

        folders.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        if (folders.Count == 0 && files.Count == 0)
        {
            menu.Add(new Label("Empty folder.") { TextColor = Shell.Theme.TextMuted });
            return;
        }

        foreach (DirectoryEntry entry in folders)
            menu.Add(new Button($"[folder]  {entry.Name}", () => EnterFolder(FullPath(entry.Name))));

        foreach (DirectoryEntry entry in files)
        {
            string label = entry.Name;
            long size = TryGetFileSize(FullPath(entry.Name));
            if (size > 0)
                label += "   " + Formatting.FormatSize(size);
            menu.Add(new Button(label, () => ReturnPath(FullPath(entry.Name))));
        }
    }

    private static long TryGetFileSize(string path)
    {
        try
        {
            return FileSystem.GetFileSize(path);
        }
        catch
        {
            if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            {
                var stat = SandboxBroker.Stat(path);
                if (stat.Ok)
                    return stat.Size;
            }
            return 0;
        }
    }

    private bool AcceptsFile(string fileName)
    {
        if (_options.Extensions.Count == 0)
            return true;

        int dot = fileName.LastIndexOf('.');
        if (dot < 0)
            return false;

        string extension = fileName[dot..];
        foreach (string allowed in _options.Extensions)
        {
            if (string.Equals(extension, allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private string FullPath(string name) => _current.TrimEnd('/') + "/" + name;

    private void EnterFolder(string path)
    {
        _current = path;
        _atRoot = false;
        RebuildScreen();
    }

    private void ReturnPath(string? path)
    {
        if (_returned)
            return;
        _returned = true;
        Shell.Pop();
        _picked(path);
    }

    // The path one level above <paramref name="path"/>, or the empty string when the path is a root
    // slash. A root ("/") has no parent, and returning it here would loop the "go up" action.
    private static string ParentOf(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return "";
        string trimmed = path.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        if (slash <= 0)
            return "/";
        return trimmed[..slash];
    }

    // Checks whether a folder is enumerable either directly or through the broker. Used before
    // entering or accepting a folder path so a typed /data path succeeds even when the direct
    // enumerate returns EINVAL because the partition is not bound to the module's namespace.
    private static bool CanEnumerateFolder(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        if (FileSystem.TryEnumerateDirectory(path, out _, out _))
            return true;
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            return SandboxBroker.IsDirectory(path);
        return false;
    }

    private static bool BrokerFileExists(string path)
        => SandboxBroker.IsOnBrokerPartition(path)
            && SandboxBroker.IsReachable()
            && SandboxBroker.FileExists(path);

    /// <summary>Convenience entry point: open the picker in file mode.</summary>
    public static void PickFile(
        MultiToolsShell shell,
        string title,
        string startingPath,
        IReadOnlyList<string> extensions,
        Action<string?> picked)
    {
        shell.Push(new FilePickerScreen(shell,
            new FilePickerOptions
            {
                Kind = PickerKind.File,
                Title = title,
                StartingPath = startingPath,
                Extensions = extensions,
            },
            picked));
    }

    /// <summary>Convenience entry point: open the picker in folder mode.</summary>
    public static void PickFolder(
        MultiToolsShell shell,
        string title,
        string startingPath,
        Action<string?> picked)
    {
        shell.Push(new FilePickerScreen(shell,
            new FilePickerOptions
            {
                Kind = PickerKind.Folder,
                Title = title,
                StartingPath = startingPath,
            },
            picked));
    }
}
