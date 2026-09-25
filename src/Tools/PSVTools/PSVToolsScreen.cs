// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Data;
using ProsperoMultiTools.Shell;
using SharpProspero.Storage;
using SharpProspero.Storage.Sfo;
using SharpProspero.Ui;
using System;

namespace ProsperoMultiTools.Tools.PSVTools;

internal sealed class PSVToolsScreen : MultiToolsScreen
{
    public PSVToolsScreen(MultiToolsShell shell) : base(shell) { }

    public override string Title => "PS Vita Utilities";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 500 };

        menu.Add(new Label("Tools for PS Vita backups.") { TextColor = Shell.Theme.TextMuted });
        menu.Add(new Separator());

        menu.Add(new Button("Read PS Vita SFO", () =>
        {
            FilePickerScreen.PickFile(Shell, "Pick a param.sfo",
                Places.StartingPoint(Shell.Settings.StartPath) ?? "",
                [".sfo"],
                path => { if (!string.IsNullOrEmpty(path)) ShowSfoInfo(path); });
        }));

        menu.Add(new Button("Browse PS Vita backup folder", () =>
        {
            FilePickerScreen.PickFolder(Shell, "Pick a PS Vita backup folder",
                Places.StartingPoint(Shell.Settings.StartPath) ?? "",
                path => { if (!string.IsNullOrEmpty(path))
                    Shell.Push(new Browser.BackupBrowserScreen(Shell, GamePlatform.PSVita, path)); });
        }));

        menu.Add(new Separator());
        menu.Add(new Button("View PKG info", () =>
        {
            FilePickerScreen.PickFile(Shell, "Pick a .pkg file",
                Places.StartingPoint(Shell.Settings.StartPath) ?? "",
                [".pkg"],
                path => { if (!string.IsNullOrEmpty(path)) ShowPkgInfo(path); });
        }));

        return menu;
    }

    private void ShowSfoInfo(string path)
    {
        SfoFile? sfo = SfoFile.ReadFromFile(path);
        if (sfo is null)
        {
            Shell.Notify("Could not read SFO file.");
            return;
        }

        int lastSlash = path.LastIndexOf('/');
        string parentDir = lastSlash > 0 ? path.Substring(0, lastSlash) : "";

        var info = new BackupInfo
        {
            Platform = GamePlatform.PSVita,
            FileType = BackupFileType.Folder,
            Title = sfo.GetString("TITLE") ?? "",
            GameId = sfo.GetString("TITLE_ID") ?? "",
            ContentId = sfo.GetString("CONTENT_ID") ?? "",
            Category = BackupInfo.MapPSVCategory(sfo.GetString("CATEGORY") ?? ""),
            Version = sfo.GetString("APP_VER") ?? "",
            FolderPath = parentDir,
            FilePath = path,
            Sfo = sfo,
        };
        info.Region = BackupInfo.DetectRegion(info.GameId);

        if (parentDir.Length > 0)
        {
            string iconPath = $"{parentDir}/icon0.png";
            if (FileSystem.Exists(iconPath))
                info.IconPath = iconPath;

            string bgPath = $"{parentDir}/pic0.png";
            if (FileSystem.Exists(bgPath))
                info.BackgroundPath = bgPath;
        }

        Shell.Push(new Browser.BackupDetailScreen(Shell, info));
    }

    private void ShowPkgInfo(string path)
    {
        PkgInfo? pkg = PkgReader.ReadPkgInfo(path);
        if (pkg is null)
        {
            Shell.Notify("Could not read PKG file.");
            return;
        }

        var info = new BackupInfo
        {
            Platform = GamePlatform.PSVita,
            FileType = BackupFileType.Pkg,
            Title = pkg.Title,
            GameId = pkg.TitleId,
            ContentId = pkg.ContentId,
            Size = pkg.FileSize,
            FilePath = path,
            Param = pkg.Param,
        };
        info.Region = BackupInfo.DetectRegion(info.GameId);

        Shell.Push(new Browser.BackupDetailScreen(Shell, info));
    }
}
