// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Data;
using ProsperoMultiTools.Shell;
using SharpProspero.Storage;
using SharpProspero.Storage.Sfo;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;

namespace ProsperoMultiTools.Tools.PSPTools;

internal sealed class PSPToolsScreen : MultiToolsScreen
{
    public PSPToolsScreen(MultiToolsShell shell) : base(shell) { }

    public override string Title => "PSP Utilities";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 500 };

        menu.Add(new Label("Tools for PSP backups.") { TextColor = Shell.Theme.TextMuted });
        menu.Add(new Separator());

        menu.Add(new Button("Read PSP SFO", () =>
        {
            FilePickerScreen.PickFile(Shell, "Pick a PARAM.SFO",
                Places.StartingPoint(Shell.Settings.StartPath) ?? "",
                [".sfo"],
                path => { if (!string.IsNullOrEmpty(path)) ShowSfoInfo(path); });
        }));

        menu.Add(new Button("Scan PSP ISO info", () =>
        {
            FilePickerScreen.PickFile(Shell, "Pick a PSP .iso",
                Places.StartingPoint(Shell.Settings.StartPath) ?? "",
                [".iso", ".cso"],
                path => { if (!string.IsNullOrEmpty(path)) ShowIsoInfo(path); });
        }));

        menu.Add(new Button("Browse PSP backup folder", () =>
        {
            FilePickerScreen.PickFolder(Shell, "Pick a PSP backup folder",
                Places.StartingPoint(Shell.Settings.StartPath) ?? "",
                path => { if (!string.IsNullOrEmpty(path))
                    Shell.Push(new Browser.BackupBrowserScreen(Shell, GamePlatform.PSP, path)); });
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

        var info = new BackupInfo
        {
            Platform = GamePlatform.PSP,
            FileType = BackupFileType.Folder,
            Title = sfo.GetString("TITLE") ?? "",
            GameId = sfo.GetString("DISC_ID") ?? sfo.GetString("TITLE_ID") ?? "",
            Category = BackupInfo.MapPSPCategory(sfo.GetString("CATEGORY") ?? ""),
            Version = sfo.GetString("DISC_VERSION") ?? "",
            FirmwareVersion = sfo.GetString("PSP_SYSTEM_VER") ?? "",
            Sfo = sfo,
        };
        info.Region = BackupInfo.DetectRegion(info.GameId);

        // Derive media paths from the SFO location when it sits inside a PSP_GAME folder.
        string? gameRoot = DerivePSPGameRoot(path);
        if (gameRoot is not null)
        {
            info.FolderPath = gameRoot;

            string iconPath = $"{gameRoot}/PSP_GAME/ICON0.PNG";
            if (FileSystem.Exists(iconPath))
                info.IconPath = iconPath;

            string bgPath = $"{gameRoot}/PSP_GAME/PIC1.PNG";
            if (FileSystem.Exists(bgPath))
                info.BackgroundPath = bgPath;

            string sndPath = $"{gameRoot}/PSP_GAME/SND0.AT3";
            if (FileSystem.Exists(sndPath))
                info.SoundtrackPath = sndPath;
        }

        Shell.Push(new Browser.BackupDetailScreen(Shell, info));
    }

    private void ShowIsoInfo(string path)
    {
        if (!FileSystem.Exists(path))
        {
            Shell.Notify("File not found.");
            return;
        }

        IsoVolumeDescriptor? vol = IsoReader.ReadVolumeDescriptor(path);
        if (vol is null)
        {
            Shell.Notify("Not a valid ISO file.");
            return;
        }

        var info = new BackupInfo
        {
            Platform = GamePlatform.PSP,
            FileType = BackupFileType.Iso,
            Title = vol.VolumeId,
            Size = FileSystem.GetFileSize(path),
            FilePath = path,
        };

        // Walk the ISO directory tree to read PSP_GAME/PARAM.SFO for real metadata.
        byte[]? sfoData = IsoReader.ReadFile(path, "PSP_GAME/PARAM.SFO");
        if (sfoData is not null)
        {
            SfoFile? sfo = SfoFile.Read(sfoData);
            if (sfo is not null)
            {
                info.Title = sfo.GetString("TITLE") ?? vol.VolumeId;
                info.GameId = sfo.GetString("DISC_ID") ?? sfo.GetString("TITLE_ID") ?? "";
                info.Category = BackupInfo.MapPSPCategory(sfo.GetString("CATEGORY") ?? "");
                info.Version = sfo.GetString("DISC_VERSION") ?? "";
                info.FirmwareVersion = sfo.GetString("PSP_SYSTEM_VER") ?? "";
                info.Region = BackupInfo.DetectRegion(info.GameId);
                info.Sfo = sfo;
            }
        }

        Shell.Push(new Browser.BackupDetailScreen(Shell, info));
    }

    /// <summary>
    /// Given a path like ".../PSP_GAME/PARAM.SFO", returns the game root directory
    /// (the parent of PSP_GAME). Returns <c>null</c> when the path does not contain PSP_GAME.
    /// </summary>
    private static string? DerivePSPGameRoot(string sfoPath)
    {
        int idx = sfoPath.LastIndexOf("/PSP_GAME/", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? sfoPath.Substring(0, idx) : null;
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
            Platform = GamePlatform.PSP,
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
