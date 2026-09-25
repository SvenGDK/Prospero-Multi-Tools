// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Shell;
using SharpProspero.Compression;
using SharpProspero.Interop;
using SharpProspero.Storage;
using SharpProspero.Storage.Sfo;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace ProsperoMultiTools.Data;

internal static class BackupScanner
{
    public static List<BackupInfo> ScanFolder(string path, GamePlatform platform, int maxDepth = 2, CancellationToken cancel = default)
    {
        var results = new List<BackupInfo>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        ScanRecursive(path, platform, results, 0, maxDepth, visited, cancel);
        return results;
    }

    private static void ScanRecursive(string path, GamePlatform platform, List<BackupInfo> results, int depth, int maxDepth, HashSet<string> visited, CancellationToken cancel)
    {
        if (depth > maxDepth)
            return;

        if (cancel.IsCancellationRequested)
            return;

        // Track visited directories to prevent symlink loops.
        if (!visited.Add(path))
            return;

        IReadOnlyList<DirectoryEntry> entries;
        bool useBroker = false;
        if (!FileSystem.TryEnumerateDirectory(path, out entries!, out _))
        {
            // The direct call fails with EINVAL when the path lies on a partition the module's
            // mount namespace does not bind (/data, /user, etc.). The broker can enumerate those
            // paths because the daemon runs outside the sandbox.
            if (!SandboxBroker.IsOnBrokerPartition(path) || !SandboxBroker.IsReachable())
                return;

            var brokerList = SandboxBroker.List(path);
            if (brokerList.Outcome != BrokerOutcome.Ok || brokerList.Entries.Count == 0)
                return;
            useBroker = true;

            var converted = new List<DirectoryEntry>(brokerList.Entries.Count);
            foreach (BrokerDirEntry be in brokerList.Entries)
            {
                FileEntryType kind = be.IsDirectory ? FileEntryType.Directory
                    : be.IsFile ? FileEntryType.File
                    : FileEntryType.Unknown;
                converted.Add(new DirectoryEntry { Name = be.Name, Type = kind });
            }
            entries = converted;
        }

        foreach (DirectoryEntry entry in entries)
        {
            if (cancel.IsCancellationRequested)
                return;

            string fullPath = $"{path}/{entry.Name}";

            // Skip auxiliary per-track .bin files from multi-track PS1 backups. The .cue
            // sibling is the canonical entry and lists every track already; opening a bare
            // "Foo (Track 03).bin" as its own backup produces noise or crashes the SYSTEM.CNF
            // scan on tracks that only carry CDDA audio. The extension gate already ignores
            // .bin for platform switches that need a .cue, but this explicit skip covers the
            // path unambiguously so future readers do not have to trace the flow.
            if (entry.Type == FileEntryType.File && IsPs1TrackSidecar(entry.Name))
                continue;

            bool isDirectory;
            if (entry.Type == FileEntryType.Directory)
            {
                isDirectory = true;
            }
            else if (entry.Type == FileEntryType.Unknown)
            {
                // The listing did not report a kind; ask the broker or the file system directly.
                isDirectory = useBroker
                    ? SandboxBroker.IsDirectory(fullPath)
                    : FileSystem.GetEntryType(fullPath) == FileEntryType.Directory;
            }
            else
            {
                isDirectory = false;
            }

            if (isDirectory)
            {
                try
                {
                    BackupInfo? folderBackup = useBroker
                        ? TryReadFolderBackupViaBroker(fullPath, platform)
                        : TryReadFolderBackup(fullPath, platform);
                    if (folderBackup is not null)
                    {
                        results.Add(folderBackup);
                        continue;
                    }
                }
                // Broad catch: any unexpected exception from a folder classifier - a corrupt
                // sce_sys, an unreadable param.sfo, an ISO helper that throws on a truncated
                // header, an out-of-memory from a 7 MB SYSTEM.CNF scan - must not tear down
                // the whole scan. The offending folder is silently skipped and the walker
                // continues into its children.
                catch (Exception) { }

                ScanRecursive(fullPath, platform, results, depth + 1, maxDepth, visited, cancel);
            }
            else
            {
                try
                {
                    BackupInfo? fileBackup = TryReadFileBackup(fullPath, entry.Name, platform, useBroker);
                    if (fileBackup is not null)
                        results.Add(fileBackup);
                }
                // Broad catch for the same reason as the folder branch: an unexpected exception
                // from a file reader - a malformed .cue, a Track .bin whose SYSTEM.CNF scan
                // trips an ISO helper, a PKG whose AES walk hits a corrupted key - is treated
                // as "this file is not a backup" rather than "the scan is over".
                catch (Exception) { }
            }
        }
    }

    // True when the file name looks like an auxiliary per-track .bin from a multi-track PS1
    // backup ("Rayman (USA) (Track 03).bin", "Mickey (Track 21).bin"). Recognises the standard
    // dumper conventions: "(Track NN)" with one or two digits, optionally preceded by "Track"
    // as a keyword. The .cue sibling drives the backup read; this file is metadata to that,
    // not a backup on its own. Case-insensitive so "(track 03).bin" and "(TRACK 03).bin" match.
    private static bool IsPs1TrackSidecar(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return false;
        string ext = GetExtension(fileName);
        if (ext != ".bin")
            return false;

        // Walk the name looking for "(Track NN)" or "(track NN)" where NN is one or two digits.
        // A minimal state machine keeps this cheap and does not allocate.
        for (int i = 0; i + 8 < fileName.Length; i++)
        {
            if (fileName[i] != '(')
                continue;
            int p = i + 1;
            if (p + 5 >= fileName.Length)
                return false;
            if (!(fileName[p] == 'T' || fileName[p] == 't')) continue;
            if (!(fileName[p + 1] == 'r' || fileName[p + 1] == 'R')) continue;
            if (!(fileName[p + 2] == 'a' || fileName[p + 2] == 'A')) continue;
            if (!(fileName[p + 3] == 'c' || fileName[p + 3] == 'C')) continue;
            if (!(fileName[p + 4] == 'k' || fileName[p + 4] == 'K')) continue;
            p += 5;
            // Skip one whitespace.
            while (p < fileName.Length && (fileName[p] == ' ' || fileName[p] == '\t'))
                p++;
            // Require at least one digit.
            int digitStart = p;
            while (p < fileName.Length && fileName[p] >= '0' && fileName[p] <= '9')
                p++;
            if (p == digitStart)
                continue;
            // Trailing whitespace + close paren.
            while (p < fileName.Length && (fileName[p] == ' ' || fileName[p] == '\t'))
                p++;
            if (p < fileName.Length && fileName[p] == ')')
                return true;
        }
        return false;
    }

    // Reads folder backup metadata through the broker for paths on partitions the module's
    // mount namespace does not bind. PS4 and PS5 folder backups carry sce_sys/ metadata
    // that the broker can read through SandboxBroker.ReadAllBytes and parse from byte arrays.
    // PS3, PSP, and PSVita backups are disc-image-heavy and rarely live under /data; they fall
    // through to null so the scan continues into sub-directories.
    private static BackupInfo? TryReadFolderBackupViaBroker(string folderPath, GamePlatform platform)
    {
        return platform switch
        {
            GamePlatform.PS4 => TryReadPS4FolderViaBroker(folderPath),
            GamePlatform.PS5 => TryReadPS5FolderViaBroker(folderPath),
            _ => null,
        };
    }

    private static BackupInfo? TryReadPS4FolderViaBroker(string path)
    {
        string sfoPath = $"{path}/sce_sys/param.sfo";
        var (outcome, bytes) = SandboxBroker.ReadAllBytes(sfoPath);
        if (outcome != BrokerOutcome.Ok || bytes.Length == 0)
            return null;

        SfoFile? sfo = SfoFile.Read(bytes);
        if (sfo is null)
            return null;

        string gameId = sfo.GetString("TITLE_ID") ?? "";
        if (PlatformClassifier.FromTitleId(gameId) != GamePlatform.PS4)
            return null;

        string rawCategory = sfo.GetString("CATEGORY") ?? "";
        if (!IsPS4ApplicationCategory(rawCategory))
            return null;

        var info = new BackupInfo
        {
            Platform = GamePlatform.PS4,
            FileType = BackupFileType.Folder,
            Title = NormalizeTitle(sfo.GetString("TITLE") ?? ""),
            GameId = gameId,
            ContentId = sfo.GetString("CONTENT_ID") ?? "",
            Region = BackupInfo.DetectRegion(gameId),
            Category = BackupInfo.MapPS4Category(rawCategory),
            Version = sfo.GetString("APP_VER") ?? "",
            FirmwareVersion = sfo.GetString("SYSTEM_VER") ?? "",
            FolderPath = path,
            Sfo = sfo,
        };

        string iconPath = $"{path}/sce_sys/icon0.png";
        if (SandboxBroker.FileExists(iconPath))
            info.IconPath = iconPath;

        string bgPath = $"{path}/sce_sys/pic0.png";
        if (SandboxBroker.FileExists(bgPath))
            info.BackgroundPath = bgPath;

        string sndPath = $"{path}/sce_sys/snd0.at9";
        if (SandboxBroker.FileExists(sndPath))
            info.SoundtrackPath = sndPath;

        return info;
    }

    private static BackupInfo? TryReadPS5FolderViaBroker(string path)
    {
        string paramPath = $"{path}/sce_sys/param.json";
        var (outcome, bytes) = SandboxBroker.ReadAllBytes(paramPath);
        if (outcome != BrokerOutcome.Ok || bytes.Length == 0)
            return null;

        ParamJson? param = ParamJson.ReadFromBytes(bytes);
        if (param is null)
            return null;

        var info = new BackupInfo
        {
            Platform = GamePlatform.PS5,
            FileType = BackupFileType.Folder,
            Title = NormalizeTitle(param.DisplayTitle),
            GameId = param.TitleId,
            ContentId = param.ContentId,
            Region = BackupInfo.DetectRegion(param.TitleId),
            Category = MapPS5CategoryType(param.ApplicationCategoryType),
            Version = param.ContentVersion,
            FirmwareVersion = param.RequiredSystemSoftwareVersion,
            FolderPath = path,
            Param = param,
        };

        string iconPath = $"{path}/sce_sys/icon0.png";
        if (SandboxBroker.FileExists(iconPath))
            info.IconPath = iconPath;

        string bgPath = $"{path}/sce_sys/pic0.png";
        if (SandboxBroker.FileExists(bgPath))
            info.BackgroundPath = bgPath;

        string sndPath = $"{path}/sce_sys/snd0.at9";
        if (SandboxBroker.FileExists(sndPath))
            info.SoundtrackPath = sndPath;

        return info;
    }

    private static BackupInfo? TryReadFolderBackup(string folderPath, GamePlatform platform)
    {
        switch (platform)
        {
            case GamePlatform.PS3:
                return TryReadPS3Folder(folderPath);
            case GamePlatform.PS4:
                return TryReadPS4Folder(folderPath);
            case GamePlatform.PS5:
                return TryReadPS5Folder(folderPath);
            case GamePlatform.PSP:
                return TryReadPSPFolder(folderPath);
            case GamePlatform.PSVita:
                return TryReadPSVFolder(folderPath);
            default:
                return null;
        }
    }

    private static BackupInfo? TryReadFileBackup(string filePath, string fileName, GamePlatform platform, bool useBroker)
    {
        string ext = GetExtension(fileName);

        switch (platform)
        {
            case GamePlatform.PS1:
                // BIN/CUE disc images only. The .cue entry represents the whole set; a bare .bin
                // referenced by a .cue nearby is picked up when the .cue is read, not on its own.
                if (ext == ".cue")
                    return TryReadCueBackup(filePath, platform, useBroker);
                break;
            case GamePlatform.PS2:
                // ISO disc images only. The extension gate is filename-only; the on-disc content
                // check that follows keeps a PSP UMD image saved as .iso out of the PS2 list.
                if (ext == ".iso")
                {
                    if (!DiscInspector.BelongsTo(filePath, GamePlatform.PS2))
                        return null;
                    return TryReadIsoBackup(filePath, platform, useBroker);
                }
                break;
            case GamePlatform.PS3:
                if (ext == ".iso")
                {
                    if (!DiscInspector.BelongsTo(filePath, GamePlatform.PS3))
                        return null;
                    return TryReadIsoBackup(filePath, platform, useBroker);
                }
                if (ext == ".pkg")
                    return TryReadPkgBackup(filePath, platform, useBroker);
                break;
            case GamePlatform.PSP:
                if (ext == ".iso" || ext == ".cso")
                {
                    // A .cso is a compressed UMD image whose header carries the PSP signature but
                    // the ISO-level filesystem is compressed and unreadable to the shell-side
                    // reader; accept it on extension alone. An .iso passes the on-disc content
                    // check so a PS2 disc saved as .iso stays out of the PSP list.
                    if (ext == ".iso" && !DiscInspector.BelongsTo(filePath, GamePlatform.PSP))
                        return null;
                    return TryReadIsoBackup(filePath, platform, useBroker);
                }
                if (ext == ".pkg")
                    return TryReadPkgBackup(filePath, platform, useBroker);
                if (ext == ".pbp")
                    return TryReadPbpBackup(filePath, useBroker);
                break;
            case GamePlatform.PS4:
            case GamePlatform.PS5:
                if (ext == ".pkg")
                    return TryReadPkgBackup(filePath, platform, useBroker);
                break;
            case GamePlatform.PSVita:
                if (ext == ".pkg")
                    return TryReadPkgBackup(filePath, platform, useBroker);
                if (ext == ".vpk")
                    return TryReadVpkBackup(filePath, useBroker);
                break;
        }

        return null;
    }

    private static BackupInfo? TryReadPS3Folder(string path)
    {
        string sfoDir = $"{path}/PS3_GAME";
        string sfoPath = $"{sfoDir}/PARAM.SFO";
        if (!FileSystem.Exists(sfoPath))
        {
            sfoDir = path;
            sfoPath = $"{path}/PARAM.SFO";
            if (!FileSystem.Exists(sfoPath))
                return null;
        }

        SfoFile? sfo = SfoFile.ReadFromFile(sfoPath);
        if (sfo is null)
            return null;

        string gameId = sfo.GetString("TITLE_ID") ?? "";
        if (PlatformClassifier.FromTitleId(gameId) != GamePlatform.PS3)
            return null;

        string rawCategory = sfo.GetString("CATEGORY") ?? "";
        if (!IsPS3ApplicationCategory(rawCategory))
            return null;

        var info = new BackupInfo
        {
            Platform = GamePlatform.PS3,
            FileType = BackupFileType.Folder,
            Title = NormalizeTitle(sfo.GetString("TITLE") ?? ""),
            GameId = gameId,
            ContentId = sfo.GetString("CONTENT_ID") ?? "",
            Region = BackupInfo.DetectRegion(gameId),
            Category = BackupInfo.MapPS3Category(rawCategory),
            Version = sfo.GetString("APP_VER") ?? sfo.GetString("VERSION") ?? "",
            FolderPath = path,
            Sfo = sfo,
        };

        string iconPath = $"{sfoDir}/ICON0.PNG";
        if (FileSystem.Exists(iconPath))
            info.IconPath = iconPath;

        string bgPath = $"{sfoDir}/PIC1.PNG";
        if (FileSystem.Exists(bgPath))
            info.BackgroundPath = bgPath;

        string sndPath = $"{sfoDir}/SND0.AT3";
        if (FileSystem.Exists(sndPath))
            info.SoundtrackPath = sndPath;

        return info;
    }

    private static BackupInfo? TryReadPS4Folder(string path)
    {
        string sfoPath = $"{path}/sce_sys/param.sfo";
        if (!FileSystem.Exists(sfoPath))
            return null;

        SfoFile? sfo = SfoFile.ReadFromFile(sfoPath);
        if (sfo is null)
            return null;

        string gameId = sfo.GetString("TITLE_ID") ?? "";
        if (PlatformClassifier.FromTitleId(gameId) != GamePlatform.PS4)
            return null;

        string rawCategory = sfo.GetString("CATEGORY") ?? "";
        if (!IsPS4ApplicationCategory(rawCategory))
            return null;

        var info = new BackupInfo
        {
            Platform = GamePlatform.PS4,
            FileType = BackupFileType.Folder,
            Title = NormalizeTitle(sfo.GetString("TITLE") ?? ""),
            GameId = gameId,
            ContentId = sfo.GetString("CONTENT_ID") ?? "",
            Region = BackupInfo.DetectRegion(gameId),
            Category = BackupInfo.MapPS4Category(rawCategory),
            Version = sfo.GetString("APP_VER") ?? "",
            FirmwareVersion = sfo.GetString("SYSTEM_VER") ?? "",
            FolderPath = path,
            Sfo = sfo,
        };

        string iconPath = $"{path}/sce_sys/icon0.png";
        if (FileSystem.Exists(iconPath))
            info.IconPath = iconPath;

        string bgPath = $"{path}/sce_sys/pic0.png";
        if (FileSystem.Exists(bgPath))
            info.BackgroundPath = bgPath;

        string sndPath = $"{path}/sce_sys/snd0.at9";
        if (FileSystem.Exists(sndPath))
            info.SoundtrackPath = sndPath;

        return info;
    }

    private static BackupInfo? TryReadPS5Folder(string path)
    {
        string paramPath = $"{path}/sce_sys/param.json";
        if (!FileSystem.Exists(paramPath))
            return null;

        ParamJson? param = ParamJson.ReadFromFile(paramPath);
        if (param is null)
            return null;

        var info = new BackupInfo
        {
            Platform = GamePlatform.PS5,
            FileType = BackupFileType.Folder,
            Title = NormalizeTitle(param.DisplayTitle),
            GameId = param.TitleId,
            ContentId = param.ContentId,
            Region = BackupInfo.DetectRegion(param.TitleId),
            Category = MapPS5CategoryType(param.ApplicationCategoryType),
            Version = param.ContentVersion,
            FirmwareVersion = param.RequiredSystemSoftwareVersion,
            FolderPath = path,
            Param = param,
        };

        string iconPath = $"{path}/sce_sys/icon0.png";
        if (FileSystem.Exists(iconPath))
            info.IconPath = iconPath;

        string bgPath = $"{path}/sce_sys/pic0.png";
        if (FileSystem.Exists(bgPath))
            info.BackgroundPath = bgPath;

        string sndPath = $"{path}/sce_sys/snd0.at9";
        if (FileSystem.Exists(sndPath))
            info.SoundtrackPath = sndPath;

        return info;
    }

    private static BackupInfo? TryReadPSPFolder(string path)
    {
        string sfoDir = $"{path}/PSP_GAME";
        string sfoPath = $"{sfoDir}/PARAM.SFO";
        if (!FileSystem.Exists(sfoPath))
        {
            sfoDir = path;
            sfoPath = $"{path}/PARAM.SFO";
            if (!FileSystem.Exists(sfoPath))
                return null;
        }

        SfoFile? sfo = SfoFile.ReadFromFile(sfoPath);
        if (sfo is null)
            return null;

        string gameId = sfo.GetString("DISC_ID") ?? sfo.GetString("TITLE_ID") ?? "";
        if (PlatformClassifier.FromTitleId(gameId) != GamePlatform.PSP)
            return null;

        var info = new BackupInfo
        {
            Platform = GamePlatform.PSP,
            FileType = BackupFileType.Folder,
            Title = NormalizeTitle(sfo.GetString("TITLE") ?? ""),
            GameId = gameId,
            Region = BackupInfo.DetectRegion(gameId),
            Category = BackupInfo.MapPSPCategory(sfo.GetString("CATEGORY") ?? ""),
            Version = sfo.GetString("DISC_VERSION") ?? "",
            FolderPath = path,
            Sfo = sfo,
        };

        string iconPath = $"{sfoDir}/ICON0.PNG";
        if (FileSystem.Exists(iconPath))
            info.IconPath = iconPath;

        string bgPath = $"{sfoDir}/PIC1.PNG";
        if (FileSystem.Exists(bgPath))
            info.BackgroundPath = bgPath;

        string sndPath = $"{sfoDir}/SND0.AT3";
        if (FileSystem.Exists(sndPath))
            info.SoundtrackPath = sndPath;

        return info;
    }

    private static BackupInfo? TryReadPSVFolder(string path)
    {
        string sfoPath = $"{path}/sce_sys/param.sfo";
        if (!FileSystem.Exists(sfoPath))
            return null;

        SfoFile? sfo = SfoFile.ReadFromFile(sfoPath);
        if (sfo is null)
            return null;

        string gameId = sfo.GetString("TITLE_ID") ?? "";
        if (PlatformClassifier.FromTitleId(gameId) != GamePlatform.PSVita)
            return null;

        var info = new BackupInfo
        {
            Platform = GamePlatform.PSVita,
            FileType = BackupFileType.Folder,
            Title = NormalizeTitle(sfo.GetString("TITLE") ?? ""),
            GameId = gameId,
            ContentId = sfo.GetString("CONTENT_ID") ?? "",
            Region = BackupInfo.DetectRegion(gameId),
            Category = BackupInfo.MapPSVCategory(sfo.GetString("CATEGORY") ?? ""),
            Version = sfo.GetString("APP_VER") ?? "",
            FolderPath = path,
            Sfo = sfo,
        };

        string iconPath = $"{path}/sce_sys/icon0.png";
        if (FileSystem.Exists(iconPath))
            info.IconPath = iconPath;

        string bgPath = $"{path}/sce_sys/pic0.png";
        if (FileSystem.Exists(bgPath))
            info.BackgroundPath = bgPath;

        return info;
    }

    private static BackupInfo? TryReadCueBackup(string cuePath, GamePlatform platform, bool useBroker)
    {
        CueSheet? cue;
        if (useBroker)
        {
            var (outcome, bytes) = SandboxBroker.ReadAllBytes(cuePath);
            if (outcome != BrokerOutcome.Ok || bytes.Length == 0)
                return null;
            cue = CueSheet.Parse(Encoding.UTF8.GetString(bytes));
        }
        else
        {
            cue = CueSheet.ParseFromFile(cuePath);
        }
        if (cue is null)
            return null;

        string fileName = GetFileName(cuePath);
        string baseName = TrimExtension(fileName);
        string folder = cue.GetDirectory(cuePath);

        // Compute the total on-disk footprint of the backup: the .cue itself plus every raw file
        // it references (data track and every audio track). Each file is stat'd through the same
        // broker-aware helper the scan uses everywhere else, and the file body is never opened,
        // read, or mapped. A stat that fails (missing sidecar, unreachable file) contributes zero
        // and does not tear down the scan.
        long totalSize = SafeStatSize(cuePath, useBroker);
        List<string> referenced = cue.GetBinFilePathsResolved(cuePath, path => PathExistsSafe(path, useBroker));
        foreach (string referencedPath in referenced)
            totalSize += SafeStatSize(referencedPath, useBroker);

        var info = new BackupInfo
        {
            Platform = platform,
            FileType = BackupFileType.BinCue,
            Title = NormalizeTitle(baseName),
            Size = totalSize,
            FilePath = cuePath,
            FolderPath = folder,
        };

        // Real serial derivation: pull SYSTEM.CNF from the first data track through the raw-CD
        // ISO 9660 walker. A retail PS1 disc's SYSTEM.CNF names the boot executable
        // ("BOOT = cdrom:\SLES_012.34;1"), which the extractor converts to the hyphenated form
        // ("SLES-01234") the region look-up and cover library both key by. Only the first
        // referenced file is opened, and only the sectors the walker actually needs (PVD, root
        // directory, SYSTEM.CNF) are read - the file body past the ISO 9660 entries is never
        // touched, and audio-only "(Track NN).bin" sidecars are never opened by this path. A
        // walker miss (image on a compressed variant, unusual mode, corrupted PVD) falls through
        // to the filename-and-database look-up so a game with a recognisable name still surfaces
        // its serial and region.
        if (platform == GamePlatform.PS1 && referenced.Count > 0)
            TryFillPs1SerialFromSystemCnf(info, referenced[0]);
        if (platform == GamePlatform.PS2 && referenced.Count > 0 && string.IsNullOrEmpty(info.GameId))
            TryFillPs2SerialFromSystemCnf(info, referenced[0]);

        ExtractDiscSerial(info, baseName);
        if (platform == GamePlatform.PS1 && string.IsNullOrEmpty(info.GameId))
        {
            string looked = GameIdDatabase.LookupPs1SerialByTitle(baseName);
            if (!string.IsNullOrEmpty(looked))
            {
                info.GameId = looked;
                info.Region = BackupInfo.DetectRegion(looked);
            }
        }
        ApplyDiscTitle(info);
        ApplySidecarAssets(info, folder, baseName, platform, useBroker);
        return info;
    }

    // Returns the size of the file at <paramref name="path"/> through the broker for partitions
    // the mount namespace does not bind and through the direct stat call for everywhere else.
    // Returns 0 on any failure; the caller adds zero to the running total so a missing sidecar
    // never propagates as an exception through the scan.
    private static long SafeStatSize(string path, bool preferBroker)
    {
        if (preferBroker && SandboxBroker.IsReachable() && SandboxBroker.IsOnBrokerPartition(path))
        {
            BrokerStat brokerStat = SandboxBroker.Stat(path);
            if (brokerStat.Ok && brokerStat.Size >= 0)
                return brokerStat.Size;
        }
        try
        {
            if (FileSystem.Exists(path))
                return FileSystem.GetFileSize(path);
        }
        catch (ProsperoException) { }

        if (SandboxBroker.IsReachable() && SandboxBroker.IsOnBrokerPartition(path))
        {
            BrokerStat brokerStat = SandboxBroker.Stat(path);
            if (brokerStat.Ok && brokerStat.Size >= 0)
                return brokerStat.Size;
        }
        return 0;
    }

    // Broker-aware existence probe used by the cue resolver so a candidate that would only
    // resolve through the broker (a backup stored under /data or /user) is still preferred over
    // its basename fallback in the cue's own folder.
    private static bool PathExistsSafe(string path, bool preferBroker)
    {
        if (preferBroker && SandboxBroker.IsReachable() && SandboxBroker.IsOnBrokerPartition(path))
        {
            if (SandboxBroker.FileExists(path))
                return true;
        }
        try
        {
            if (FileSystem.Exists(path))
                return true;
        }
        catch (ProsperoException) { }
        if (SandboxBroker.IsReachable() && SandboxBroker.IsOnBrokerPartition(path))
            return SandboxBroker.FileExists(path);
        return false;
    }

    // Opens the first cue-referenced file as a raw CD image, walks ISO 9660 for SYSTEM.CNF, and
    // extracts the disc serial from its BOOT line. Fills info.GameId and info.Region when it
    // finds one. Any failure (open refused, sector 16 not a PVD, SYSTEM.CNF missing, BOOT line
    // malformed) leaves info untouched so the filename / database fallbacks below can run.
    private static void TryFillPs1SerialFromSystemCnf(BackupInfo info, string firstReferencedPath)
    {
        try
        {
            using RandomAccessByteSource? source = RandomAccessByteSource.Open(firstReferencedPath);
            if (source is null)
                return;

            byte[]? systemCnf = BinIsoReader.ReadFile(source, "SYSTEM.CNF", 64 * 1024);
            if (systemCnf is null || systemCnf.Length == 0)
                return;

            string serial = BinIsoReader.ExtractSerialFromSystemCnf(systemCnf);
            if (string.IsNullOrEmpty(serial))
                return;

            info.GameId = serial;
            info.Region = BackupInfo.DetectRegion(serial);
        }
        catch (Exception)
        {
            // A read that throws is silently skipped: the filename and database fallbacks stay
            // available, and the scan continues into the next backup entry.
        }
    }

    // The PS2 disc SYSTEM.CNF carries a BOOT2 line instead of BOOT; otherwise the extractor is
    // identical. Kept separate from the PS1 path so a shared PS1/PS2 disc-serial prefix cannot
    // silently swap the file the reader parses.
    private static void TryFillPs2SerialFromSystemCnf(BackupInfo info, string firstReferencedPath)
    {
        try
        {
            using RandomAccessByteSource? source = RandomAccessByteSource.Open(firstReferencedPath);
            if (source is null)
                return;

            byte[]? systemCnf = BinIsoReader.ReadFile(source, "SYSTEM.CNF", 64 * 1024);
            if (systemCnf is null || systemCnf.Length == 0)
                return;

            string serial = BinIsoReader.ExtractSerialFromSystemCnf(systemCnf);
            if (string.IsNullOrEmpty(serial))
                return;

            info.GameId = serial;
            info.Region = BackupInfo.DetectRegion(serial);
        }
        catch (Exception)
        {
        }
    }

    private static BackupInfo? TryReadIsoBackup(string isoPath, GamePlatform platform, bool useBroker)
    {
        string fileName = GetFileName(isoPath);
        string baseName = TrimExtension(fileName);
        string folder = GetFolder(isoPath);

        // Open one byte source over the ISO and thread it through every read below. The source
        // picks the direct route for a bound partition and the broker route for /data or /user
        // on its own; a null return means the file is not reachable over either route.
        using RandomAccessByteSource? iso = RandomAccessByteSource.Open(isoPath);
        if (iso is null)
            return null;
        long fileSize = iso.Size;

        var info = new BackupInfo
        {
            Platform = platform,
            FileType = BackupFileType.Iso,
            Title = NormalizeTitle(baseName),
            Size = fileSize,
            FilePath = isoPath,
            FolderPath = folder,
        };

        IsoVolumeDescriptor? vol = IsoReader.ReadVolumeDescriptor(iso);
        if (vol is not null && !string.IsNullOrEmpty(vol.VolumeId))
        {
            info.Title = NormalizeTitle(vol.VolumeId);
        }

        // Read the platform-specific metadata carried inside the disc image so a scanner result
        // names the real game and disc id instead of the file name and lets a follow-up screen
        // read a full property set from the same disc.
        switch (platform)
        {
            case GamePlatform.PS2:
                ReadPs2IsoMetadata(info, iso);
                break;
            case GamePlatform.PSP:
                ReadPspIsoMetadata(info, iso);
                break;
            case GamePlatform.PS3:
                ReadPs3IsoMetadata(info, iso);
                break;
        }

        // For disc images whose layout carries media files (PS3, PSP), extract and cache
        // the icon, background and soundtrack so the browser preview indicators and the
        // detail screen display them without a second seek into the image.
        if (platform is GamePlatform.PS3 or GamePlatform.PSP)
            ExtractIsoMedia(info, isoPath, platform, iso);

        ExtractDiscSerial(info, baseName);
        ApplyDiscTitle(info);
        ApplySidecarAssets(info, folder, baseName, platform, useBroker);
        return info;
    }

    private static void ExtractIsoMedia(BackupInfo info, string isoPath, GamePlatform platform, RandomAccessByteSource source)
    {
        string prefix = platform == GamePlatform.PSP ? "PSP_GAME" : "PS3_GAME";
        string gameId = (info.GameId ?? "").Trim().ToUpperInvariant().Replace("-", "");
        if (gameId.Length == 0)
            return;

        string? cacheFolder = CoverService.CacheFolder();
        string? targetFolder = null;
        if (cacheFolder is not null)
        {
            targetFolder = PathUtil.Combine(cacheFolder,
                platform == GamePlatform.PSP ? "PSP" : "PS3");
            try { FileSystem.CreateDirectoryRecursive(targetFolder); }
            catch (ProsperoException)
            {
                if (SandboxBroker.IsOnBrokerPartition(targetFolder) && SandboxBroker.IsReachable())
                {
                    if (SandboxBroker.MkdirRecursive(targetFolder) != BrokerOutcome.Ok)
                        targetFolder = null;
                }
                else
                {
                    targetFolder = null;
                }
            }
        }
        if (targetFolder is null)
            targetFolder = GetFolder(isoPath);
        if (string.IsNullOrEmpty(targetFolder))
            return;

        if (string.IsNullOrEmpty(info.IconPath))
            info.IconPath = CacheIsoFile(source, prefix + "/ICON0.PNG",
                PathUtil.Combine(targetFolder, gameId + ".png"));

        if (string.IsNullOrEmpty(info.BackgroundPath))
            info.BackgroundPath = CacheIsoFile(source, prefix + "/PIC1.PNG",
                PathUtil.Combine(targetFolder, gameId + "_bg.png"));

        if (string.IsNullOrEmpty(info.SoundtrackPath))
            info.SoundtrackPath = CacheIsoFile(source, prefix + "/SND0.AT3",
                PathUtil.Combine(targetFolder, gameId + "_snd.at3"));
    }

    private static string CacheIsoFile(RandomAccessByteSource source, string internalPath, string cachedPath)
    {
        // Check if the cached file already exists. The cache folder can live on /data,
        // which the module's mount namespace does not bind; the broker handles that path.
        bool exists;
        try { exists = FileSystem.Exists(cachedPath); }
        catch (ProsperoException) { exists = false; }
        if (!exists && SandboxBroker.IsOnBrokerPartition(cachedPath) && SandboxBroker.IsReachable())
            exists = SandboxBroker.FileExists(cachedPath);
        if (exists)
            return cachedPath;

        try
        {
            byte[]? data = IsoReader.ReadFile(source, internalPath);
            if (data is not null && data.Length > 0)
            {
                bool written;
                try
                {
                    FileSystem.WriteAllBytes(cachedPath, data);
                    written = true;
                }
                catch (ProsperoException)
                {
                    written = SandboxBroker.IsOnBrokerPartition(cachedPath)
                        && SandboxBroker.IsReachable()
                        && SandboxBroker.WriteAllBytes(cachedPath, data) == BrokerOutcome.Ok;
                }
                if (written)
                    return cachedPath;
            }
        }
        catch (ProsperoException) { }

        return "";
    }

    private static void ReadPs2IsoMetadata(BackupInfo info, RandomAccessByteSource source)
    {
        string serial = DiscInspector.ReadPs2DiscSerial(source);
        if (!string.IsNullOrEmpty(serial))
        {
            info.GameId = serial;
            info.Region = BackupInfo.DetectRegion(serial);
        }
    }

    private static void ReadPspIsoMetadata(BackupInfo info, RandomAccessByteSource source)
    {
        byte[]? sfoBytes = DiscInspector.ReadPspParamSfo(source);
        if (sfoBytes is null || sfoBytes.Length == 0)
            return;

        SfoFile? sfo = SfoFile.Read(sfoBytes);
        if (sfo is null)
            return;

        info.Sfo = sfo;
        string title = sfo.GetString("TITLE") ?? "";
        if (!string.IsNullOrEmpty(title))
            info.Title = NormalizeTitle(title);

        string discId = sfo.GetString("DISC_ID") ?? sfo.GetString("TITLE_ID") ?? "";
        if (!string.IsNullOrEmpty(discId))
        {
            info.GameId = discId;
            info.Region = BackupInfo.DetectRegion(discId);
        }
        info.Category = BackupInfo.MapPSPCategory(sfo.GetString("CATEGORY") ?? "");
        info.Version = sfo.GetString("DISC_VERSION") ?? "";
    }

    private static void ReadPs3IsoMetadata(BackupInfo info, RandomAccessByteSource source)
    {
        byte[]? sfoBytes = DiscInspector.ReadPs3ParamSfo(source);
        if (sfoBytes is null || sfoBytes.Length == 0)
            return;

        SfoFile? sfo = SfoFile.Read(sfoBytes);
        if (sfo is null)
            return;

        info.Sfo = sfo;
        string title = sfo.GetString("TITLE") ?? "";
        if (!string.IsNullOrEmpty(title))
            info.Title = NormalizeTitle(title);

        string titleId = sfo.GetString("TITLE_ID") ?? "";
        if (!string.IsNullOrEmpty(titleId))
        {
            info.GameId = titleId;
            info.Region = BackupInfo.DetectRegion(titleId);
        }
        info.ContentId = sfo.GetString("CONTENT_ID") ?? "";
        info.Category = BackupInfo.MapPS3Category(sfo.GetString("CATEGORY") ?? "");
        info.Version = sfo.GetString("APP_VER") ?? sfo.GetString("VERSION") ?? "";
    }

    // Looks at the file name for the four-letter, five-digit disc serial the classic platforms carry
    // (SLES-12345, SLUS12345, and so on). When the file name carries one, it becomes the info's game
    // id and its region falls out of the id's own letters. A file name that does not carry one is
    // left as it was.
    private static void ExtractDiscSerial(BackupInfo info, string baseName)
    {
        if (!string.IsNullOrEmpty(info.GameId))
            return;

        string serial = FindDiscSerial(baseName);
        if (serial.Length == 0)
            return;

        info.GameId = serial;
        if (string.IsNullOrEmpty(info.Region))
            info.Region = BackupInfo.DetectRegion(serial);
    }

    // Fills in the display title from the on-device serial-to-title list when the file name did not
    // give a real title and the info's game id is one the list carries. A backup that already has a
    // real title (from an ISO volume label or a matching PARAM.SFO) is left as it is.
    private static void ApplyDiscTitle(BackupInfo info)
    {
        if (string.IsNullOrEmpty(info.GameId))
            return;
        if (!string.IsNullOrEmpty(info.Title)
            && !string.Equals(info.Title, info.GameId, StringComparison.OrdinalIgnoreCase)
            && info.Title.IndexOfAny(['/', '\\']) < 0
            && !info.Title.Equals(info.GameId.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
        {
            // The scanner already picked a title from the disc image itself; keep it.
            return;
        }

        string lookup = info.Platform switch
        {
            GamePlatform.PS1 => GameIdDatabase.LookupPs1(info.GameId),
            GamePlatform.PS2 => GameIdDatabase.LookupPs2(info.GameId),
            _ => string.Empty,
        };
        if (!string.IsNullOrEmpty(lookup))
            info.Title = NormalizeTitle(lookup);
    }

    // Finds the first four-letter, five-digit disc serial in the string, with or without a hyphen.
    // Returned in the hyphenated form the toolkit's serial-to-cover library keys by ("AAAA-NNNNN").
    private static string FindDiscSerial(string s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        for (int i = 0; i + 9 <= s.Length; i++)
        {
            if (!IsUpperLetter(s[i]) || !IsUpperLetter(s[i + 1])
                || !IsUpperLetter(s[i + 2]) || !IsUpperLetter(s[i + 3]))
                continue;

            int digitStart = s[i + 4] == '-' ? i + 5 : i + 4;
            if (digitStart + 5 > s.Length)
                continue;
            bool digits = true;
            for (int j = 0; j < 5; j++)
            {
                if (s[digitStart + j] is < '0' or > '9')
                {
                    digits = false;
                    break;
                }
            }
            if (!digits)
                continue;

            return string.Concat(s.AsSpan(i, 4), "-", s.AsSpan(digitStart, 5));
        }
        return string.Empty;
    }

    private static bool IsUpperLetter(char c) => c is >= 'A' and <= 'Z';

    // Looks for icon, background, and soundtrack files next to a disc image, matching either the disc's
    // own base name or the platform's own convention (ICON0.PNG, PIC1.PNG, SND0.AT3 in the same folder,
    // an ART sidecar folder). Only sidecars that are actually present are attached.
    private static void ApplySidecarAssets(BackupInfo info, string folder, string baseName, GamePlatform platform, bool useBroker)
    {
        if (string.IsNullOrEmpty(folder))
            return;

        ReadOnlySpan<string> iconNames = [
            baseName + ".png",
            baseName + ".jpg",
            baseName + ".jpeg",
            baseName + "_ICO.png",
            "ICON0.PNG",
            "icon0.png",
            "cover.png",
            "cover.jpg",
        ];
        ReadOnlySpan<string> backgroundNames = [
            baseName + "_bg.png",
            baseName + "_BG.png",
            baseName + "-bg.png",
            baseName + "_SCR.png",
            "PIC1.PNG",
            "pic1.png",
            "PIC0.PNG",
            "pic0.png",
            "background.png",
        ];
        ReadOnlySpan<string> soundtrackNames = platform is GamePlatform.PS4 or GamePlatform.PS5
            ? [baseName + ".at9", baseName + "_snd.at9", "snd0.at9", "SND0.AT9"]
            : [baseName + ".at3", baseName + "_snd.at3", "SND0.AT3", "snd0.at3"];

        info.IconPath = FirstExisting(folder, iconNames, useBroker) ?? info.IconPath;
        info.BackgroundPath = FirstExisting(folder, backgroundNames, useBroker) ?? info.BackgroundPath;
        info.SoundtrackPath = FirstExisting(folder, soundtrackNames, useBroker) ?? info.SoundtrackPath;
    }

    private static string? FirstExisting(string folder, ReadOnlySpan<string> names, bool useBroker)
    {
        foreach (string name in names)
        {
            string candidate = $"{folder}/{name}";
            if (useBroker)
            {
                if (SandboxBroker.FileExists(candidate))
                    return candidate;
            }
            else
            {
                if (FileSystem.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    private static string TrimExtension(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name.Substring(0, dot) : name;
    }

    private static string GetFolder(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash > 0 ? path.Substring(0, slash) : string.Empty;
    }

    private static BackupInfo? TryReadPkgBackup(string pkgPath, GamePlatform platform, bool useBroker)
    {
        PkgInfo? pkgInfo = PkgReader.ReadPkgInfo(pkgPath);
        if (pkgInfo is null)
            return null;

        PkgType pkgType = pkgInfo.Type;
        if (!PkgMatchesPlatform(pkgType, platform))
            return null;

        // Defensive cross-check: an NPDRM package whose classified family does not agree with
        // the platform its TITLE_ID publishes under is a corrupted or mislabeled dump. Drop it
        // rather than surface it on the wrong tab. The classic and PSM families ride on prefix
        // sets shared with sister families and are skipped: their content_type field is the
        // authoritative signal and the prefix would answer for the wrong platform.
        if (!string.IsNullOrEmpty(pkgInfo.TitleId)
            && pkgType is not PkgType.PsClassic and not PkgType.PsPsm)
        {
            GamePlatform prefixPlatform = PlatformClassifier.FromTitleId(pkgInfo.TitleId);
            if (prefixPlatform != GamePlatform.Unknown && prefixPlatform != platform)
                return null;
        }

        string folder = GetFolder(pkgPath);
        string baseName = TrimExtension(GetFileName(pkgPath));

        string title = !string.IsNullOrEmpty(pkgInfo.Title) ? pkgInfo.Title : pkgInfo.TitleId;
        var info = new BackupInfo
        {
            Platform = platform,
            FileType = BackupFileType.Pkg,
            Title = NormalizeTitle(title),
            GameId = pkgInfo.TitleId,
            ContentId = pkgInfo.ContentId,
            Region = BackupInfo.DetectRegion(pkgInfo.TitleId),
            Category = MapPkgCategory(platform, pkgInfo.Category),
            Version = !string.IsNullOrEmpty(pkgInfo.AppVersion) ? pkgInfo.AppVersion : "",
            Size = pkgInfo.FileSize,
            FilePath = pkgPath,
            FolderPath = folder,
            Param = pkgInfo.Param,
        };

        // Package readers cache an extracted ICON0.PNG to the shared cover folder for the
        // families they can walk (PS3, PSP); use that as the starting point so a later
        // sidecar match can still take over when the folder ships its own cover.
        if (!string.IsNullOrEmpty(pkgInfo.IconPath))
            info.IconPath = pkgInfo.IconPath;

        ApplySidecarAssets(info, folder, baseName, platform, useBroker);
        return info;
    }

    // Maps a raw PARAM.SFO CATEGORY code from a package to the display string for the package's
    // platform. Returns the raw code unchanged for platforms whose CATEGORY taxonomy is not
    // mapped (PS5 uses a numeric category type from param.json rather than an SFO string).
    private static string MapPkgCategory(GamePlatform platform, string rawCategory)
    {
        if (string.IsNullOrEmpty(rawCategory))
            return "";
        return platform switch
        {
            GamePlatform.PS3 => BackupInfo.MapPS3Category(rawCategory),
            GamePlatform.PSP => BackupInfo.MapPSPCategory(rawCategory),
            GamePlatform.PSVita => BackupInfo.MapPSVCategory(rawCategory),
            GamePlatform.PS4 => BackupInfo.MapPS4Category(rawCategory),
            _ => rawCategory,
        };
    }

    private static bool PkgMatchesPlatform(PkgType pkgType, GamePlatform platform)
    {
        return platform switch
        {
            // PS1-classic NPDRM packages (a PSX title running on later hardware) surface under
            // the PS1 backup manager since that is the platform the game itself targets.
            GamePlatform.PS1 => pkgType == PkgType.PsClassic,
            GamePlatform.PS3 => pkgType == PkgType.PS3,
            GamePlatform.PSP => pkgType == PkgType.PSP,
            GamePlatform.PSVita => pkgType == PkgType.PSVita || pkgType == PkgType.PsPsm,
            GamePlatform.PS4 => pkgType == PkgType.PS4,
            GamePlatform.PS5 => pkgType is PkgType.PS5Retail or PkgType.PS5Debug or PkgType.PS5Meta,
            _ => false,
        };
    }

    private static string MapPS5CategoryType(int categoryType)
    {
        return categoryType switch
        {
            0 => "Game",
            1 => "Application",
            2 => "Additional Content",
            _ => categoryType.ToString(),
        };
    }

    private static string GetExtension(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        if (dot < 0)
            return "";
        string ext = fileName.Substring(dot);
        return ext.Length <= 5 ? ToLower(ext) : "";
    }

    private static string GetFileName(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path.Substring(slash + 1) : path;
    }

    private static string ToLower(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(c >= 'A' && c <= 'Z' ? (char)(c + 32) : c);
        return sb.ToString();
    }

    // The set of PARAM.SFO CATEGORY codes that identify an installable PS4 application backup.
    // A folder whose SFO carries a different code (a save file's SD, a theme's TH) still holds
    // a TITLE_ID that would otherwise pass the prefix gate, and would surface as a false game
    // in the PS4 browser without this check.
    private static bool IsPS4ApplicationCategory(string code)
    {
        return code switch
        {
            "ac" or "bd" or "gc" or "gd" or "gda" or "gdb" or "gdc" or "gdd" or "gde"
                or "gdg" or "gdgd" or "gdk" or "gdl" or "gd0" or "gp" => true,
            _ => false,
        };
    }

    // The set of PARAM.SFO CATEGORY codes that identify an installable PS3 application backup.
    // Every code the PS3 folder-scan surfaces belongs to a Game, Data, or add-on class; save
    // files (SD, MS) are accepted here so a user browsing an exported /dev_hdd0/home tree does
    // not lose track of them, but the launcher already treats them as inspect-only.
    private static bool IsPS3ApplicationCategory(string code)
    {
        return code switch
        {
            "DG" or "HG" or "GD" or "SD" or "MS" or "AT" or "CB" or "AP" => true,
            _ => false,
        };
    }

    // Cleans a raw title string for display. Handles the anomalies that show up in real backups:
    // interior NULs that leak from an SFO string with a MaxLength greater than the actual bytes,
    // CR/LF pairs a two-line PSP display title carries, runs of consecutive whitespace, and
    // parenthesised region or edition tags a re-dumper appended to the filename ("(JP)",
    // "(v1.02)"). Returns the empty string when the input is null or reduces to whitespace.
    internal static string NormalizeTitle(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var sb = new StringBuilder(raw.Length);
        bool lastWasSpace = false;
        foreach (char c in raw)
        {
            // NUL and line-break characters both collapse to a single space so a title that
            // arrived as "Game Title\r\nSubtitle" reads as "Game Title Subtitle".
            char normalised = c switch
            {
                '\0' or '\r' or '\n' or '\t' => ' ',
                _ => c,
            };
            if (normalised == ' ')
            {
                if (lastWasSpace || sb.Length == 0)
                    continue;
                sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(normalised);
                lastWasSpace = false;
            }
        }

        // Strip a trailing space introduced by the last character.
        while (sb.Length > 0 && sb[^1] == ' ')
            sb.Length--;

        // Strip parenthesised tags a filename or dumper appended to the real title. Nested and
        // unmatched parens leave the string untouched to avoid mangling a title that legitimately
        // uses them.
        return StripParenthesisedTags(sb.ToString());
    }

    // Removes "(...)" and " (...)" spans from a title, collapsing the whitespace that surrounds
    // them. A missing closing parenthesis stops the pass early to keep the rest of the title
    // intact rather than truncating at an accidental open paren.
    private static string StripParenthesisedTags(string s)
    {
        if (s.IndexOf('(') < 0)
            return s;
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '(')
            {
                int close = s.IndexOf(')', i + 1);
                if (close < 0)
                {
                    sb.Append(s, i, s.Length - i);
                    break;
                }
                // Drop a single space that led into the parenthesised tag so we do not leave a
                // double space where the tag used to be.
                if (sb.Length > 0 && sb[^1] == ' ')
                    sb.Length--;
                i = close + 1;
                continue;
            }
            sb.Append(s[i]);
            i++;
        }
        // A leftover trailing space from a stripped tail parenthesis.
        while (sb.Length > 0 && sb[^1] == ' ')
            sb.Length--;
        return sb.ToString();
    }

    // Reads a PSP homebrew EBOOT.PBP or similar single-file container. The .pbp is a fixed
    // 40-byte header of a magic word, a version word, and eight little-endian file offsets that
    // point at optional PARAM.SFO, ICON0.PNG, ICON1.PMF, PIC0.PNG, PIC1.PNG, SND0.AT3, DATA.PSP
    // and DATA.PSAR sections in that order. The reader walks only the PARAM.SFO and ICON0.PNG
    // sections; the two data blobs (the compiled PSP executable and, on a PSX Classic, the
    // encrypted disc image) stay in place until an emulator opens them separately.
    private static BackupInfo? TryReadPbpBackup(string pbpPath, bool useBroker)
    {
        _ = useBroker; // The random-access source picks the right route on its own.

        using RandomAccessByteSource? source = RandomAccessByteSource.Open(pbpPath);
        if (source is null)
            return null;

        long fileSize = source.Size;
        if (fileSize < PbpHeaderSize)
            return null;

        Span<byte> header = stackalloc byte[PbpHeaderSize];
        int read = source.ReadAt(0, header);
        if (read < PbpHeaderSize)
            return null;

        // Magic bytes "\0PBP".
        if (header[0] != 0x00 || header[1] != 0x50 || header[2] != 0x42 || header[3] != 0x50)
            return null;

        // Eight little-endian section offsets at bytes [0x08..0x27]. offset[7] is the DATA.PSAR
        // start and the file's tail cap for section-size math.
        Span<uint> offsets = stackalloc uint[PbpSectionCount];
        for (int i = 0; i < PbpSectionCount; i++)
            offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(PbpFirstOffsetField + i * 4, 4));

        uint sfoStart = offsets[PbpSfoSection];
        uint sfoEnd = offsets[PbpIcon0Section];
        if (sfoStart < PbpHeaderSize || sfoEnd <= sfoStart || sfoEnd > fileSize)
            return null;

        long sfoLength = (long)sfoEnd - sfoStart;
        if (sfoLength <= 0 || sfoLength > MaxPbpSfoBytes)
            return null;

        byte[] sfoBytes = new byte[sfoLength];
        int sfoRead = source.ReadAt(sfoStart, sfoBytes);
        if (sfoRead < sfoLength)
            return null;

        SfoFile? sfo = SfoFile.Read(sfoBytes);
        if (sfo is null)
            return null;

        string rawCategory = sfo.GetString("CATEGORY") ?? "";
        if (!IsPspPbpCategory(rawCategory))
            return null;

        string gameId = sfo.GetString("DISC_ID") ?? sfo.GetString("TITLE_ID") ?? "";
        string folder = GetFolder(pbpPath);

        // File-type carries no dedicated PBP value in the shared enum; the .Pkg value classifies
        // the .pbp as a single-file container so the browser's file-type filter keeps it out of
        // the disc-image row and the launcher treats it as inspect-only until a PSP homebrew
        // launcher can accept it directly.
        var info = new BackupInfo
        {
            Platform = GamePlatform.PSP,
            FileType = BackupFileType.Pkg,
            Title = NormalizeTitle(sfo.GetString("TITLE") ?? string.Empty),
            GameId = gameId,
            Region = BackupInfo.DetectRegion(gameId),
            Category = BackupInfo.MapPSPCategory(rawCategory),
            Version = sfo.GetString("DISC_VERSION") ?? sfo.GetString("APP_VER") ?? string.Empty,
            Size = fileSize,
            FilePath = pbpPath,
            FolderPath = folder,
            Sfo = sfo,
        };

        // Cache the packaged icon so the browser tile has a real cover without walking the file
        // a second time. A missing ICON0 section, an unreachable cover cache, or a write failure
        // all reduce to leaving info.IconPath empty; CoverService.Resolve falls through to the
        // shared library on demand.
        string? cachedIcon = TryCachePbpIcon(source, offsets[PbpIcon0Section], offsets[PbpIcon1Section], gameId);
        if (!string.IsNullOrEmpty(cachedIcon))
            info.IconPath = cachedIcon;

        string baseName = TrimExtension(GetFileName(pbpPath));
        ApplySidecarAssets(info, folder, baseName, GamePlatform.PSP, useBroker);
        return info;
    }

    // Reads a PS Vita .vpk homebrew package. A .vpk is a plain ZIP holding at least sce_sys/
    // param.sfo and eboot.bin; the scanner walks only the SFO to name the app and to reject a
    // file whose title-id belongs to a different family (a mislabeled dump). The archive body
    // stays untouched.
    private static BackupInfo? TryReadVpkBackup(string vpkPath, bool useBroker)
    {
        using RandomAccessByteSource? source = RandomAccessByteSource.Open(vpkPath);
        if (source is null)
            return null;

        long fileSize = source.Size;
        if (fileSize <= 0 || fileSize > MaxVpkBytes)
            return null;

        byte[] archiveBytes = new byte[fileSize];
        int total = 0;
        while (total < fileSize)
        {
            int chunk = source.ReadAt(total, archiveBytes.AsSpan(total));
            if (chunk <= 0)
                return null;
            total += chunk;
        }

        ZipArchive archive;
        try
        {
            archive = ZipArchive.Open(archiveBytes);
        }
        catch (CompressionException)
        {
            return null;
        }

        if (!TryLocatePathCaseInsensitive(archive, "sce_sys/param.sfo", out ZipEntry? sfoEntry) || sfoEntry is null)
            return null;

        byte[] sfoBytes;
        try
        {
            sfoBytes = archive.Extract(sfoEntry);
        }
        catch (CompressionException)
        {
            return null;
        }
        if (sfoBytes.Length == 0)
            return null;

        SfoFile? sfo = SfoFile.Read(sfoBytes);
        if (sfo is null)
            return null;

        string gameId = sfo.GetString("TITLE_ID") ?? "";
        if (PlatformClassifier.FromTitleId(gameId) != GamePlatform.PSVita)
            return null;

        string folder = GetFolder(vpkPath);
        string rawCategory = sfo.GetString("CATEGORY") ?? "";

        // File-type carries no dedicated VPK value in the shared enum; the .Pkg value classifies
        // the .vpk as a single-file container so the browser's file-type filter keeps it out of
        // the disc-image row.
        var info = new BackupInfo
        {
            Platform = GamePlatform.PSVita,
            FileType = BackupFileType.Pkg,
            Title = NormalizeTitle(sfo.GetString("TITLE") ?? string.Empty),
            GameId = gameId,
            ContentId = sfo.GetString("CONTENT_ID") ?? string.Empty,
            Region = BackupInfo.DetectRegion(gameId),
            Category = BackupInfo.MapPSVCategory(rawCategory),
            Version = sfo.GetString("APP_VER") ?? string.Empty,
            Size = fileSize,
            FilePath = vpkPath,
            FolderPath = folder,
            Sfo = sfo,
        };

        // Cache the packaged icon (sce_sys/icon0.png when the archive carries it) so the browser
        // tile has a cover before CoverService walks the shared library.
        string? cachedIcon = TryCacheVpkIcon(archive, gameId);
        if (!string.IsNullOrEmpty(cachedIcon))
            info.IconPath = cachedIcon;

        string baseName = TrimExtension(GetFileName(vpkPath));
        ApplySidecarAssets(info, folder, baseName, GamePlatform.PSVita, useBroker);
        return info;
    }

    // Case-insensitive lookup for a well-known archive path so a .vpk that stores its metadata
    // as "sce_sys/PARAM.SFO" reads the same as one that stores it as "sce_sys/param.sfo".
    private static bool TryLocatePathCaseInsensitive(ZipArchive archive, string wanted, out ZipEntry? entry)
    {
        foreach (ZipEntry candidate in archive.Entries)
        {
            if (string.Equals(candidate.Name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                entry = candidate;
                return true;
            }
        }
        entry = null;
        return false;
    }

    private static readonly ReadOnlyMemory<string> PspPbpCategoryCodes = new(["MG", "UG", "EG", "MS"]);

    private static bool IsPspPbpCategory(string code)
    {
        if (string.IsNullOrEmpty(code))
            return false;
        ReadOnlySpan<string> allowed = PspPbpCategoryCodes.Span;
        for (int i = 0; i < allowed.Length; i++)
        {
            if (string.Equals(allowed[i], code, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private const int PbpHeaderSize = 40;
    private const int PbpFirstOffsetField = 0x08;
    private const int PbpSectionCount = 8;
    private const int PbpSfoSection = 0;
    private const int PbpIcon0Section = 1;
    private const int PbpIcon1Section = 2;

    // A PARAM.SFO past this size is not a real SFO; the format's practical ceiling for a homebrew
    // container is well under 64 KiB. The bound stops a corrupted header whose offsets claim a
    // multi-megabyte SFO from asking for a matching allocation.
    private const long MaxPbpSfoBytes = 128L * 1024L;

    // Vita homebrew packages ship as low-megabyte-range .vpk files; the scanner still runs on a
    // ~512 MiB ceiling so a mislabelled game dump that happens to sit in a .vpk-named file cannot
    // pull the whole console memory into one array.
    private const long MaxVpkBytes = 512L * 1024L * 1024L;

    // Extracts ICON0.PNG from the PBP's own bytes and writes it into the shared cover cache under
    // the normalised game id. Returns the cached path on success; an empty string on any failure
    // (missing section, unreachable cache, write refusal) so the caller carries on without a
    // cover.
    private static string TryCachePbpIcon(RandomAccessByteSource source, uint iconStart, uint iconEnd, string gameId)
    {
        if (iconEnd <= iconStart)
            return string.Empty;

        long iconLength = (long)iconEnd - iconStart;
        if (iconLength <= 0 || iconLength > MaxCachedIconBytes)
            return string.Empty;

        string? cacheFolder = CoverService.CacheFolder();
        if (cacheFolder is null)
            return string.Empty;

        string platformFolder = PathUtil.Combine(cacheFolder, "PSP");
        string key = (gameId ?? string.Empty).Trim().ToUpperInvariant().Replace("-", "");
        if (key.Length == 0)
            return string.Empty;

        string cachedPath = PathUtil.Combine(platformFolder, key + ".png");
        if (FileExistsAny(cachedPath))
            return cachedPath;

        byte[] iconBytes = new byte[iconLength];
        int read = source.ReadAt(iconStart, iconBytes);
        if (read < iconLength)
            return string.Empty;
        if (!LooksLikePng(iconBytes))
            return string.Empty;

        if (!EnsureCacheDirectory(platformFolder))
            return string.Empty;
        if (!WriteCachedIcon(cachedPath, iconBytes))
            return string.Empty;
        return cachedPath;
    }

    private static string TryCacheVpkIcon(ZipArchive archive, string titleId)
    {
        if (!TryLocatePathCaseInsensitive(archive, "sce_sys/icon0.png", out ZipEntry? iconEntry) || iconEntry is null)
            return string.Empty;
        if (iconEntry.UncompressedSize <= 0 || iconEntry.UncompressedSize > MaxCachedIconBytes)
            return string.Empty;

        string? cacheFolder = CoverService.CacheFolder();
        if (cacheFolder is null)
            return string.Empty;

        string platformFolder = PathUtil.Combine(cacheFolder, "PSVita");
        string key = (titleId ?? string.Empty).Trim().ToUpperInvariant();
        if (key.Length == 0)
            return string.Empty;

        string cachedPath = PathUtil.Combine(platformFolder, key + ".png");
        if (FileExistsAny(cachedPath))
            return cachedPath;

        byte[] iconBytes;
        try
        {
            iconBytes = archive.Extract(iconEntry);
        }
        catch (CompressionException)
        {
            return string.Empty;
        }
        if (iconBytes.Length == 0 || !LooksLikePng(iconBytes))
            return string.Empty;

        if (!EnsureCacheDirectory(platformFolder))
            return string.Empty;
        if (!WriteCachedIcon(cachedPath, iconBytes))
            return string.Empty;
        return cachedPath;
    }

    // A cover cache entry above 8 MiB is not a real icon; the reader stops rather than let a
    // malformed header ask for a matching allocation.
    private const long MaxCachedIconBytes = 8L * 1024L * 1024L;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static bool LooksLikePng(byte[] bytes)
    {
        if (bytes.Length < PngSignature.Length)
            return false;
        for (int i = 0; i < PngSignature.Length; i++)
        {
            if (bytes[i] != PngSignature[i])
                return false;
        }
        return true;
    }

    private static bool FileExistsAny(string path)
    {
        try
        {
            if (FileSystem.Exists(path))
                return true;
        }
        catch (ProsperoException) { }
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            return SandboxBroker.FileExists(path);
        return false;
    }

    // Creates the cache subfolder, routing through the broker for partitions the module cannot
    // bind. Returns true on success or when the folder is already present.
    private static bool EnsureCacheDirectory(string path)
    {
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
        {
            if (SandboxBroker.IsDirectory(path))
                return true;
            return SandboxBroker.MkdirRecursive(path) == BrokerOutcome.Ok;
        }
        try
        {
            FileSystem.CreateDirectoryRecursive(path);
            return true;
        }
        catch (ProsperoException)
        {
            return false;
        }
    }

    private static bool WriteCachedIcon(string path, byte[] bytes)
    {
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            return SandboxBroker.WriteAllBytes(path, bytes) == BrokerOutcome.Ok;
        try
        {
            FileSystem.WriteAllBytes(path, bytes);
            return true;
        }
        catch (ProsperoException)
        {
            return false;
        }
    }
}
