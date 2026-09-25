// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Shell;
using SharpProspero.Interop;
using SharpProspero.Platform;
using SharpProspero.Storage;
using System;

namespace ProsperoMultiTools.Data;

/// <summary>
/// Resolves a cover image for a backup: the file the scan itself found in the backup's own folder
/// first, then a shared library the toolkit keeps online for the classic platforms whose disc images
/// do not carry an icon. Every network answer is cached to the local data folder so a folder scanned
/// once does not walk the network again.
/// </summary>
/// <remarks>
/// The classic library keeps its covers as JPEG named by disc serial with a hyphen ("SLES-01234.jpg"),
/// grouped by platform. A backup whose game id can be normalised to that form gets a cover; the rest
/// keep whatever the scan itself found. The service picks nothing at random, and reports failure by
/// returning an empty string so a caller carries on with the local answer.
/// </remarks>
internal static class CoverService
{
    private const string LibraryBase = "https://raw.githubusercontent.com/SvenGDK/PSMT-Covers/main/";

    private static readonly object HttpLock = new();
    private static HttpClient? _http;

    /// <summary>
    /// The cache folder covers land in. Uses the same data folder <see cref="AppSettings"/> writes to,
    /// so the covers a scan pulled once are reachable the next time the same folder is scanned.
    /// </summary>
    /// <returns>The folder path when a writable data folder is reachable, otherwise null.</returns>
    public static string? CacheFolder()
    {
        string? data = Places.DataFolder("prospero-multi-tools");
        if (data is null)
            return null;
        string covers = PathUtil.Combine(data, "covers");
        return EnsureDirectory(covers) ? covers : null;
    }

    // Creates a directory, routing through the broker when the target lies on a partition the
    // module's mount namespace does not bind. Returns true when the directory exists at the end
    // of the call, whether it was created here or was already present.
    private static bool EnsureDirectory(string path)
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

    // Reports whether a path holds a readable file, routing through the broker for /data-family
    // paths so the cache lookup does not miss a file the daemon can read from a partition the
    // module cannot bind directly.
    private static bool FileExistsAny(string path)
    {
        if (FileSystem.Exists(path))
            return true;
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            return SandboxBroker.FileExists(path);
        return false;
    }

    // Writes bytes to disk, routing through the broker for /data-family paths so a cache write
    // succeeds against the daemon's namespace when the module's own write returns EINVAL. Returns
    // true on success.
    private static bool WriteBytes(string path, byte[] bytes)
    {
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
        {
            return SandboxBroker.WriteAllBytes(path, bytes) == BrokerOutcome.Ok;
        }
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

    /// <summary>
    /// Returns the on-disk path of a cover for <paramref name="info"/>, downloading and caching it
    /// when only the library carries one. An empty string means no cover is available and the caller
    /// should carry on without one.
    /// </summary>
    public static string Resolve(BackupInfo info)
    {
        if (info is null)
            return string.Empty;

        if (!string.IsNullOrEmpty(info.IconPath) && FileExistsAny(info.IconPath))
            return info.IconPath;

        // A disc-image backup for a platform whose ISO layout carries an icon inside the disc
        // takes the icon straight out of the image rather than reaching the network. PSP puts
        // ICON0.PNG at PSP_GAME/ICON0.PNG, PS3 at PS3_GAME/ICON0.PNG; both are read once and
        // then cached like a network-fetched cover so the scanner does not re-read a large ISO
        // on every browse.
        string? isoExtracted = TryExtractIsoIcon(info);
        if (!string.IsNullOrEmpty(isoExtracted))
            return isoExtracted;

        string? libraryPath = LibraryFolderFor(info.Platform);
        if (libraryPath is null || string.IsNullOrEmpty(info.GameId))
            return string.Empty;

        (string coverKey, string coverExt) = CoverKey(info);
        if (coverKey.Length == 0)
            return string.Empty;

        string platformFolder;
        string? cacheFolder = CacheFolder();
        if (cacheFolder is not null)
        {
            platformFolder = PathUtil.Combine(cacheFolder, libraryPath);
            if (!EnsureDirectory(platformFolder))
                return string.Empty;
        }
        else
        {
            // When the shared cache is not reachable (e.g. /data returns EINVAL), cache the
            // cover beside the backup file so a network fetch still produces a displayable
            // result rather than silently dropping the answer.
            string folder = !string.IsNullOrEmpty(info.FolderPath)
                ? info.FolderPath
                : GetParentFolder(info.FilePath);
            if (string.IsNullOrEmpty(folder))
                return string.Empty;
            platformFolder = folder;
        }

        string coverPath = PathUtil.Combine(platformFolder, coverKey + coverExt);
        if (FileExistsAny(coverPath))
            return coverPath;

        string url = LibraryBase + libraryPath + "/" + coverKey + coverExt;
        if (!TryFetch(url, out byte[] bytes))
            return string.Empty;

        if (!WriteBytes(coverPath, bytes))
            return string.Empty;

        return coverPath;
    }

    // The classic library keeps a folder per platform ("PS1", "PS2", ...). PS4/PS5 backups carry
    // their own icon inside the folder so the network answer is not needed for them; PSP disc
    // backups do not sit in the library, so their game id maps to nothing.
    private static string? LibraryFolderFor(GamePlatform platform) => platform switch
    {
        GamePlatform.PS1 => "PS1",
        GamePlatform.PS2 => "PS2",
        GamePlatform.PS3 => "PS3",
        GamePlatform.PSVita => "PSVita",
        _ => null,
    };

    // Every library platform lays its covers out slightly differently. PS1, PS2 and PS3 key by the
    // disc serial in the hyphenated "AAAA-NNNNN" form and use .jpg; PSVita keys by the plain title
    // id (no hyphen) and uses .png. Both shapes are drawn out of one place so the cache path and
    // the URL agree on the same key.
    private static (string Key, string Extension) CoverKey(BackupInfo info)
    {
        if (info.Platform == GamePlatform.PSVita)
        {
            string raw = (info.GameId ?? string.Empty).Trim().ToUpperInvariant();
            return (raw, ".png");
        }
        return (NormalizeDiscSerial(info.GameId), ".jpg");
    }

    // The folder <paramref name="path"/> sits in, or an empty string when the path has no
    // separator. Matches BackupScanner.GetFolder's own semantics so the fallback cache path lands
    // next to the backup file.
    private static string GetParentFolder(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        int slash = path.LastIndexOf('/');
        return slash > 0 ? path[..slash] : string.Empty;
    }

    // The library keys covers by disc serial in the "AAAA-NNNNN" form (four letters, a hyphen, five
    // digits). A game id that has no letters or that already has a hyphen is left as it is; a bare
    // "AAAANNNNN" is split with a hyphen inserted at position four; anything else is returned empty.
    // NPDRM identifiers (a PS1-classic PKG's title id such as "NPUZ00284") share the nine-character
    // shape but are indexed differently in the shared library, so a plain shape normalisation would
    // yield a URL that always answers 404. Those ids are treated as unmatchable here so the caller
    // stops after the local sidecar check and does not walk the network on every list selection.
    private static string NormalizeDiscSerial(string gameId)
    {
        if (string.IsNullOrEmpty(gameId))
            return string.Empty;

        string trimmed = gameId.Trim();
        if (trimmed.Contains('-'))
        {
            string withHyphen = trimmed.ToUpperInvariant();
            return IsNpdrmTitleId(withHyphen) ? string.Empty : withHyphen;
        }

        if (trimmed.Length == 9)
        {
            string composed = (trimmed[..4] + "-" + trimmed[4..]).ToUpperInvariant();
            return IsNpdrmTitleId(composed) ? string.Empty : composed;
        }

        // Not a shape the library knows.
        return string.Empty;
    }

    // NPDRM title ids for PS1-classic and PSN releases start with "NP" followed by a region
    // letter and a subgroup letter. The classic-cover library keys entries by the original disc
    // serial (SLES-XXXXX, SCUS-XXXXX, PCPX-XXXXX, ...), never by NPDRM ids, so a normalisation
    // result that starts with "NP" is a certain miss and is treated as no serial rather than a
    // library key.
    private static bool IsNpdrmTitleId(string normalised)
    {
        return normalised.Length >= 4
            && normalised[0] == 'N' && normalised[1] == 'P';
    }

    // Reads ICON0.PNG straight out of a PSP or PS3 ISO and writes it into the cover cache. Returns
    // the cached path on success, or null when the file type or platform does not carry an in-disc
    // icon and the caller should fall through to the network fetch.
    private static string? TryExtractIsoIcon(BackupInfo info)
    {
        if (info.FileType != BackupFileType.Iso || string.IsNullOrEmpty(info.FilePath))
            return null;
        string? internalPath = info.Platform switch
        {
            GamePlatform.PSP => "PSP_GAME/ICON0.PNG",
            GamePlatform.PS3 => "PS3_GAME/ICON0.PNG",
            _ => null,
        };
        if (internalPath is null || string.IsNullOrEmpty(info.GameId))
            return null;

        string? cacheFolder = CacheFolder();
        if (cacheFolder is null)
            return null;

        string platformFolder = PathUtil.Combine(cacheFolder,
            info.Platform == GamePlatform.PSP ? "PSP" : "PS3");
        string key = info.GameId.Trim().ToUpperInvariant().Replace("-", "");
        if (key.Length == 0)
            return null;

        string cachedPath = PathUtil.Combine(platformFolder, key + ".png");
        if (FileExistsAny(cachedPath))
            return cachedPath;

        byte[]? iconBytes;
        try
        {
            iconBytes = IsoReader.ReadFile(info.FilePath, internalPath);
        }
        catch (ProsperoException)
        {
            return null;
        }
        if (iconBytes is null || iconBytes.Length == 0)
            return null;

        if (!EnsureDirectory(platformFolder))
            return null;
        if (!WriteBytes(cachedPath, iconBytes))
            return null;
        return cachedPath;
    }

    private static bool TryFetch(string url, out byte[] bytes)
    {
        bytes = [];
        try
        {
            HttpClient client = GetClient();
            HttpResponse response = client.Get(url);
            if (response.StatusCode is < 200 or >= 300)
                return false;
            if (response.Body is null || response.Body.Length == 0)
                return false;
            bytes = response.Body;
            return true;
        }
        catch (ProsperoException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static HttpClient GetClient()
    {
        HttpClient? existing = System.Threading.Volatile.Read(ref _http);
        if (existing is not null)
            return existing;

        lock (HttpLock)
        {
            _http ??= HttpClient.Create("ProsperoMultiTools/1.0");
            return _http;
        }
    }
}
