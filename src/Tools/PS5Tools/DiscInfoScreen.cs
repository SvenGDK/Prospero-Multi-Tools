// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Data;
using ProsperoMultiTools.Shell;
using SharpProspero.Platform;
using SharpProspero.Storage;
using SharpProspero.Ui;

namespace ProsperoMultiTools.Tools.PS5Tools;

internal sealed class DiscInfoScreen : MultiToolsScreen
{
    public DiscInfoScreen(MultiToolsShell shell) : base(shell) { }

    public override string Title => "Disc Info";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 500 };

        menu.Add(new Label("Read information from the inserted disc.") { TextColor = Shell.Theme.TextMuted });
        menu.Add(new Separator());

        bool mounted = DiscDrive.IsDiscMounted;
        menu.Add(new KeyValueRow("Disc mounted", mounted ? "Yes" : "No"));

        if (mounted)
        {
            menu.Add(new Separator());

            string? paramPath = FindDiscParamJson();
            if (paramPath is not null)
            {
                ParamJson? param = ParamJson.ReadFromFile(paramPath);
                if (param is not null)
                {
                    menu.Add(new Label("Disc param.json") { TextColor = Shell.Theme.Accent });
                    menu.Add(new KeyValueRow("Title ID", param.TitleId));
                    menu.Add(new KeyValueRow("Content ID", param.ContentId));
                    menu.Add(new KeyValueRow("Title", param.DisplayTitle));
                    menu.Add(new KeyValueRow("Version", param.ContentVersion));
                    menu.Add(new KeyValueRow("Master Version", param.MasterVersion));
                    menu.Add(new KeyValueRow("DRM Type", param.ApplicationDrmType));

                    if (param.LocalizedTitles.Count > 0)
                    {
                        menu.Add(new Separator());
                        menu.Add(new Label("Localized Titles") { TextColor = Shell.Theme.Accent });
                        foreach (LocalizedTitle lt in param.LocalizedTitles)
                            menu.Add(new KeyValueRow(lt.Language, lt.TitleName));
                    }
                }
                else
                {
                    menu.Add(new Label("Could not read disc param.json.") { TextColor = Shell.Theme.TextMuted });
                }
            }
            else
            {
                menu.Add(new Label("No param.json found on disc.") { TextColor = Shell.Theme.TextMuted });
            }

            menu.Add(new Separator());
            menu.Add(new Label("Disc root contents:") { TextColor = Shell.Theme.Accent });

            if (FileSystem.TryEnumerateDirectory(DiscDrive.MountPoint, out var files, out _))
            {
                foreach (var entry in files)
                {
                    string marker = entry.Type == FileEntryType.Directory ? "[DIR]" : "[FILE]";
                    menu.Add(new Label($"  {marker} {entry.Name}") { TextColor = Shell.Theme.TextMuted });
                }
            }
            else
            {
                menu.Add(new Label("Could not read disc contents.") { TextColor = Shell.Theme.TextMuted });
            }
        }
        else
        {
            menu.Add(new Label("Insert a disc and try again.") { TextColor = Shell.Theme.TextMuted });
        }

        menu.Add(new Separator());
        menu.Add(new Button("Refresh", () => RebuildScreen()));

        return menu;
    }

    /// <summary>
    /// Locates the disc's param.json. Checks the standard location first, then enumerates top-level
    /// directories for a param.json in case the disc layout differs.
    /// </summary>
    private static string? FindDiscParamJson()
    {
        string primary = DiscDrive.MountPoint + "/sce_sys/param.json";
        if (FileSystem.Exists(primary))
            return primary;

        if (!FileSystem.TryEnumerateDirectory(DiscDrive.MountPoint, out var entries, out _))
            return null;

        foreach (var entry in entries)
        {
            if (!entry.IsDirectory)
                continue;
            string candidate = $"{DiscDrive.MountPoint}/{entry.Name}/param.json";
            if (FileSystem.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
