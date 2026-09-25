// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Data;
using ProsperoMultiTools.Shell;
using SharpProspero.Interop.SystemService;
using SharpProspero.Platform;
using SharpProspero.Storage;
using SharpProspero.Storage.Sfo;
using SharpProspero.Ui;
using System;
using System.Text;

namespace ProsperoMultiTools.Tools.PS4Tools;

internal sealed class PS4ToolsScreen : MultiToolsScreen
{
    public PS4ToolsScreen(MultiToolsShell shell) : base(shell) { }

    public override string Title => "PS4 Utilities";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 500 };

        menu.Add(new Label("Tools for PS4 backups and applications.") { TextColor = Shell.Theme.TextMuted });
        menu.Add(new Separator());

        menu.Add(new Button("Read PS4 SFO", () =>
        {
            FilePickerScreen.PickFile(Shell, "Pick a param.sfo",
                Places.StartingPoint(Shell.Settings.StartPath) ?? "",
                [".sfo"],
                path => { if (!string.IsNullOrEmpty(path)) ShowSfoInfo(path); });
        }));

        menu.Add(new Button("Browse PS4 backup folder", () =>
        {
            FilePickerScreen.PickFolder(Shell, "Pick a PS4 backup folder",
                Places.StartingPoint(Shell.Settings.StartPath) ?? "",
                path => { if (!string.IsNullOrEmpty(path))
                    Shell.Push(new Browser.BackupBrowserScreen(Shell, GamePlatform.PS4, path)); });
        }));

        menu.Add(new Separator());
        menu.Add(new Button("View PKG info", () =>
        {
            FilePickerScreen.PickFile(Shell, "Pick a .pkg file",
                Places.StartingPoint(Shell.Settings.StartPath) ?? "",
                [".pkg"],
                path => { if (!string.IsNullOrEmpty(path)) ShowPkgInfo(path); });
        }));

        menu.Add(new Button("Install PKG", () =>
        {
            FilePickerScreen.PickFile(Shell, "Pick a .pkg file to install",
                Places.StartingPoint(Shell.Settings.StartPath) ?? "",
                [".pkg"],
                path => { if (!string.IsNullOrEmpty(path)) InstallPkg(path); });
        }));

        menu.Add(new Separator());
        menu.Add(new Button("Launch by title id", () =>
        {
            Shell.Dialogs.AskText("Title id (e.g. CUSA00000)", "", titleId =>
            {
                if (titleId is not null && titleId.Length > 0)
                    LaunchTitle(titleId);
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
            Platform = GamePlatform.PS4,
            FileType = BackupFileType.Folder,
            Title = sfo.GetString("TITLE") ?? "",
            GameId = sfo.GetString("TITLE_ID") ?? "",
            ContentId = sfo.GetString("CONTENT_ID") ?? "",
            Category = BackupInfo.MapPS4Category(sfo.GetString("CATEGORY") ?? ""),
            Version = sfo.GetString("APP_VER") ?? "",
            FirmwareVersion = sfo.GetString("SYSTEM_VER") ?? "",
            FolderPath = parentDir,
            FilePath = path,
            Sfo = sfo,
        };
        info.Region = BackupInfo.DetectRegion(info.GameId, info.ContentId);

        if (parentDir.Length > 0)
        {
            string iconPath = $"{parentDir}/icon0.png";
            if (FileSystem.Exists(iconPath))
                info.IconPath = iconPath;

            string bgPath = $"{parentDir}/pic0.png";
            if (FileSystem.Exists(bgPath))
                info.BackgroundPath = bgPath;

            string sndPath = $"{parentDir}/snd0.at9";
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
            Platform = GamePlatform.PS4,
            FileType = BackupFileType.Pkg,
            Title = pkg.Title,
            GameId = pkg.TitleId,
            ContentId = pkg.ContentId,
            Size = pkg.FileSize,
            FilePath = path,
            Param = pkg.Param,
        };
        info.Region = BackupInfo.DetectRegion(info.GameId, info.ContentId);

        Shell.Push(new Browser.BackupDetailScreen(Shell, info));
    }

    private unsafe void InstallPkg(string path)
    {
        if (!FileSystem.Exists(path))
        {
            Shell.Notify("File not found.");
            return;
        }

        try
        {
            using var installer = PackageInstaller.Open();
            installer.Install(path);
            Shell.Notify("Install request accepted.");
        }
        catch (Exception ex)
        {
            Shell.Notify($"Install failed: {ex.Message}");
        }
    }

    private unsafe void LaunchTitle(string titleId)
    {
        int byteCount = Encoding.UTF8.GetByteCount(titleId);
        byte* id = stackalloc byte[byteCount + 1];
        Encoding.UTF8.GetBytes(titleId, new Span<byte>(id, byteCount));
        id[byteCount] = 0;

        int rc = SystemService.sceSystemServiceLaunchApp(id, null, null);
        if (rc < 0)
            Shell.Notify($"Launch failed: {SharpProspero.Interop.SceResult.Describe(rc)}");
    }
}
