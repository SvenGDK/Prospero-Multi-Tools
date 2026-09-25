// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Shell;
using SharpProspero.Storage;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;

namespace ProsperoMultiTools;

internal sealed class CrossPlatformScreen : MultiToolsScreen
{
    public CrossPlatformScreen(MultiToolsShell shell) : base(shell) { }

    public override string Title => "Shared Utilities";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 500 };

        menu.Add(new Label("Utilities that work on every generation.") { TextColor = Shell.Theme.TextMuted });
        menu.Add(new Separator());

        menu.Add(new Button("Browse folder", () =>
        {
            FilePickerScreen.PickFolder(Shell, "Pick a folder to browse",
                Places.StartingPoint(Shell.Settings.StartPath) ?? "",
                path => { if (!string.IsNullOrEmpty(path)) BrowseFolder(path); });
        }));

        menu.Add(new Button("View file (hex)", () =>
        {
            FilePickerScreen.PickFile(Shell, "Pick a file to view",
                Places.StartingPoint(Shell.Settings.StartPath) ?? "",
                [],
                path => { if (!string.IsNullOrEmpty(path)) ViewHex(path); });
        }));

        return menu;
    }

    private void BrowseFolder(string path)
    {
        IReadOnlyList<DirectoryEntry> entries;
        try
        {
            entries = FileSystem.EnumerateDirectory(path);
        }
        catch (Exception ex)
        {
            Shell.Notify($"Cannot read folder: {ex.Message}");
            return;
        }

        if (entries.Count == 0)
        {
            Shell.Notify("Folder is empty.");
            return;
        }

        Shell.Push(new FolderListScreen(Shell, path, entries));
    }

    private void ViewHex(string path)
    {
        if (!FileSystem.Exists(path))
        {
            Shell.Notify("File not found.");
            return;
        }

        long size = FileSystem.GetFileSize(path);
        const int previewBytes = 512;
        int readSize = (int)Math.Min(size, previewBytes);
        byte[] buffer = new byte[readSize];

        using var stream = FileSystem.OpenRead(path);
        int bytesRead = stream.ReadAt(0, buffer);
        if (bytesRead <= 0)
        {
            Shell.Notify("Could not read file.");
            return;
        }

        Shell.Push(new HexViewScreen(Shell, path, buffer, bytesRead, size));
    }
}

internal sealed class FolderListScreen : MultiToolsScreen
{
    private readonly string _path;
    private readonly IReadOnlyList<DirectoryEntry> _entries;

    public FolderListScreen(MultiToolsShell shell, string path, IReadOnlyList<DirectoryEntry> entries) : base(shell)
    {
        _path = path;
        _entries = entries;
    }

    public override string Title => _path;

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 550 };

        menu.Add(new Label($"{_entries.Count} entries") { TextColor = Shell.Theme.TextMuted });
        menu.Add(new Separator());

        for (int i = 0; i < _entries.Count; i++)
        {
            DirectoryEntry entry = _entries[i];
            string prefix = entry.IsDirectory ? "[DIR] " : "      ";
            string fullPath = _path.EndsWith("/") ? $"{_path}{entry.Name}" : $"{_path}/{entry.Name}";

            if (entry.IsDirectory)
            {
                string dirPath = fullPath;
                menu.Add(new Button($"{prefix}{entry.Name}", () =>
                {
                    IReadOnlyList<DirectoryEntry> sub;
                    try
                    {
                        sub = FileSystem.EnumerateDirectory(dirPath);
                    }
                    catch (Exception ex)
                    {
                        Shell.Notify($"Cannot read folder: {ex.Message}");
                        return;
                    }

                    if (sub.Count == 0)
                    {
                        Shell.Notify("Folder is empty.");
                        return;
                    }

                    Shell.Push(new FolderListScreen(Shell, dirPath, sub));
                }));
            }
            else
            {
                string filePath = fullPath;
                menu.Add(new Button($"{prefix}{entry.Name}", () =>
                {
                    long size = 0;
                    try { size = FileSystem.GetFileSize(filePath); }
                    catch { /* size stays zero */ }
                    Shell.Notify($"{entry.Name}  ({Formatting.FormatSize(size)})");
                }));
            }
        }

        return menu;
    }
}

internal sealed class HexViewScreen : MultiToolsScreen
{
    private readonly string _path;
    private readonly byte[] _data;
    private readonly int _length;
    private readonly long _totalSize;

    public HexViewScreen(MultiToolsShell shell, string path, byte[] data, int length, long totalSize) : base(shell)
    {
        _path = path;
        _data = data;
        _length = length;
        _totalSize = totalSize;
    }

    public override string Title => "Hex View";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 550 };

        int lastSlash = _path.LastIndexOf('/');
        string fileName = lastSlash >= 0 ? _path.Substring(lastSlash + 1) : _path;
        menu.Add(new Label($"{fileName}  ({Formatting.FormatSize(_totalSize)})") { TextColor = Shell.Theme.TextMuted });

        if (_length < _totalSize)
            menu.Add(new Label($"Showing first {_length} bytes") { TextColor = Shell.Theme.TextMuted });

        menu.Add(new Separator());

        // Format hex dump: 16 bytes per line with offset, hex values, and ASCII.
        for (int offset = 0; offset < _length; offset += 16)
        {
            int lineLen = Math.Min(16, _length - offset);
            var sb = new System.Text.StringBuilder(80);

            sb.Append($"{offset:X8}  ");

            for (int j = 0; j < 16; j++)
            {
                if (j < lineLen)
                    sb.Append($"{_data[offset + j]:X2} ");
                else
                    sb.Append("   ");
                if (j == 7)
                    sb.Append(' ');
            }

            sb.Append(' ');
            for (int j = 0; j < lineLen; j++)
            {
                byte b = _data[offset + j];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }

            menu.Add(new Label(sb.ToString()));
        }

        return menu;
    }
}
