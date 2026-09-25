// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Data;
using ProsperoMultiTools.Shell;
using SharpProspero.Storage;
using SharpProspero.Storage.Sfo;
using SharpProspero.Ui;

namespace ProsperoMultiTools.Tools.PS3Tools;

internal sealed class PS3ToolsScreen : MultiToolsScreen
{
    public PS3ToolsScreen(MultiToolsShell shell) : base(shell) { }

    public override string Title => "PS3 Utilities";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 500 };

        menu.Add(new Label("Tools for PS3 backups.") { TextColor = Shell.Theme.TextMuted });
        menu.Add(new Separator());

        menu.Add(new Button("Read PS3 SFO", () =>
        {
            FilePickerScreen.PickFile(Shell, "Pick a PARAM.SFO",
                Places.StartingPoint(Shell.Settings.StartPath) ?? "",
                [".sfo"],
                path =>
                {
                    if (!string.IsNullOrEmpty(path))
                        ShowSfoInfo(path);
                });
        }));

        menu.Add(new Button("Browse PS3 backup folder", () =>
        {
            FilePickerScreen.PickFolder(Shell, "Pick a PS3 backup folder",
                Places.StartingPoint(Shell.Settings.StartPath) ?? "",
                path =>
                {
                    if (!string.IsNullOrEmpty(path))
                        Shell.Push(new Browser.BackupBrowserScreen(Shell, GamePlatform.PS3, path));
                });
        }));

        menu.Add(new Separator());
        menu.Add(new Button("View PKG info", () =>
        {
            FilePickerScreen.PickFile(Shell, "Pick a .pkg file",
                Places.StartingPoint(Shell.Settings.StartPath) ?? "",
                [".pkg"],
                path =>
                {
                    if (!string.IsNullOrEmpty(path))
                        ShowPkgInfo(path);
                });
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
            Platform = GamePlatform.PS3,
            FileType = BackupFileType.Folder,
            Title = sfo.GetString("TITLE") ?? "",
            GameId = sfo.GetString("TITLE_ID") ?? "",
            ContentId = sfo.GetString("CONTENT_ID") ?? "",
            Category = BackupInfo.MapPS3Category(sfo.GetString("CATEGORY") ?? ""),
            Version = sfo.GetString("APP_VER") ?? sfo.GetString("VERSION") ?? "",
            FolderPath = parentDir,
            FilePath = path,
            Sfo = sfo,
        };
        info.Region = BackupInfo.DetectRegion(info.GameId);

        if (parentDir.Length > 0)
        {
            string iconPath = $"{parentDir}/ICON0.PNG";
            if (FileSystem.Exists(iconPath))
                info.IconPath = iconPath;

            string bgPath = $"{parentDir}/PIC1.PNG";
            if (FileSystem.Exists(bgPath))
                info.BackgroundPath = bgPath;

            string sndPath = $"{parentDir}/SND0.AT3";
            if (FileSystem.Exists(sndPath))
                info.SoundtrackPath = sndPath;
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
            Platform = GamePlatform.PS3,
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
