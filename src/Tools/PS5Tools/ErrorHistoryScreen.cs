// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Shell;
using SharpProspero.Storage;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;

namespace ProsperoMultiTools.Tools.PS5Tools;

internal sealed class ErrorHistoryScreen : MultiToolsScreen
{
    private const string ErrorHistoryPath = "/system_data/priv/error/history";

    public ErrorHistoryScreen(MultiToolsShell shell) : base(shell) { }

    public override string Title => "Clear Error History";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 500 };

        menu.Add(new Label("Manage the system error history folder.") { TextColor = Shell.Theme.TextMuted });
        menu.Add(new Separator());

        bool exists = FileSystem.Exists(ErrorHistoryPath);
        menu.Add(new KeyValueRow("Path", ErrorHistoryPath));
        menu.Add(new KeyValueRow("Status", exists ? "Exists" : "Not accessible"));

        if (exists)
        {
            int fileCount = 0;
            long totalSize = 0;
            if (FileSystem.TryEnumerateDirectory(ErrorHistoryPath, out var entries, out _))
            {
                foreach (var entry in entries)
                {
                    fileCount++;
                    string fullPath = $"{ErrorHistoryPath}/{entry.Name}";
                    FileEntryType type = entry.Type == FileEntryType.Unknown
                        ? FileSystem.GetEntryType(fullPath)
                        : entry.Type;
                    if (type == FileEntryType.File)
                    {
                        try { totalSize += FileSystem.GetFileSize(fullPath); }
                        catch { }
                    }
                }
            }
            menu.Add(new KeyValueRow("Files", fileCount.ToString()));
            menu.Add(new KeyValueRow("Total size", Formatting.FormatSize(totalSize)));
        }

        menu.Add(new Separator());
        menu.Add(new Button("Clear error history", () =>
        {
            Shell.Dialogs.Confirm("Delete all error history files?", yes =>
            {
                if (yes)
                    ClearHistory();
            });
        }));

        if (exists)
        {
            menu.Add(new Separator());
            menu.Add(new Label("Error files:") { TextColor = Shell.Theme.Accent });

            if (FileSystem.TryEnumerateDirectory(ErrorHistoryPath, out var entries2, out _))
            {
                foreach (var entry in entries2)
                {
                    string fullPath = $"{ErrorHistoryPath}/{entry.Name}";
                    FileEntryType type = entry.Type == FileEntryType.Unknown
                        ? FileSystem.GetEntryType(fullPath)
                        : entry.Type;
                    if (type == FileEntryType.Directory)
                    {
                        menu.Add(new Label($"  [DIR] {entry.Name}") { TextColor = Shell.Theme.TextMuted });
                    }
                    else
                    {
                        long size = 0;
                        if (type == FileEntryType.File)
                        {
                            try { size = FileSystem.GetFileSize(fullPath); } catch { }
                        }
                        menu.Add(new Label($"  {entry.Name}  [{Formatting.FormatSize(size)}]") { TextColor = Shell.Theme.TextMuted });
                    }
                }
            }
        }

        return menu;
    }

    private void ClearHistory()
    {
        if (!FileSystem.TryEnumerateDirectory(ErrorHistoryPath, out var entries, out _))
        {
            Shell.Notify("Could not read the error history folder.");
            return;
        }

        int deleted = 0;
        int failed = 0;

        foreach (var entry in entries)
        {
            string fullPath = $"{ErrorHistoryPath}/{entry.Name}";
            try
            {
                FileEntryType type = entry.Type == FileEntryType.Unknown
                    ? FileSystem.GetEntryType(fullPath)
                    : entry.Type;
                if (type == FileEntryType.Directory)
                    DeleteDirectoryContents(fullPath);
                else
                    FileSystem.DeleteFile(fullPath);
                deleted++;
            }
            catch
            {
                failed++;
            }
        }

        RebuildScreen();
        Shell.Notify($"Deleted {deleted} entries.{(failed > 0 ? $" {failed} could not be removed." : "")}");
    }

    /// <summary>Removes a directory and everything inside it.</summary>
    private static void DeleteDirectoryContents(string path)
    {
        if (FileSystem.TryEnumerateDirectory(path, out var children, out _))
        {
            foreach (var child in children)
            {
                string childPath = $"{path}/{child.Name}";
                FileEntryType type = child.Type == FileEntryType.Unknown
                    ? FileSystem.GetEntryType(childPath)
                    : child.Type;
                if (type == FileEntryType.Directory)
                    DeleteDirectoryContents(childPath);
                else
                    FileSystem.DeleteFile(childPath);
            }
        }

        FileSystem.DeleteDirectory(path);
    }
}
