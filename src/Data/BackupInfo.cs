// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using SharpProspero.Storage.Sfo;
using System;
using System.Collections.Generic;

namespace ProsperoMultiTools.Data;

internal enum GamePlatform
{
    PS1,
    PS2,
    PS3,
    PS4,
    PS5,
    PSP,
    PSVita,

    /// <summary>The title-id prefix does not name a known platform.</summary>
    Unknown,
}

internal enum BackupFileType
{
    Unknown,
    BinCue,
    Iso,
    Pkg,
    Folder,
}

/// <summary>
/// What the console can do with this backup given its platform and file type.
/// </summary>
internal enum LaunchCapability
{
    /// <summary>PS5 application folder already installed, launched via the system launcher.</summary>
    Native,

    /// <summary>PS5 or PS4 PKG, installed via the package installer and then launched.</summary>
    Installable,

    /// <summary>PS2, PS1 or PSP backup that can be launched through an on-device emulator.</summary>
    Emulatable,

    /// <summary>No on-console emulator available for this platform.</summary>
    InspectOnly,
}

internal sealed class BackupInfo
{
    public GamePlatform Platform { get; set; }
    public BackupFileType FileType { get; set; }
    public string Title { get; set; } = "";
    public string GameId { get; set; } = "";
    public string ContentId { get; set; } = "";
    public string Region { get; set; } = "";
    public string Category { get; set; } = "";
    public string Version { get; set; } = "";
    public string FirmwareVersion { get; set; } = "";
    public long Size { get; set; }
    public string FilePath { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public string IconPath { get; set; } = "";
    public string BackgroundPath { get; set; } = "";
    public string SoundtrackPath { get; set; } = "";
    public SfoFile? Sfo { get; set; }
    public ParamJson? Param { get; set; }

    public string DisplayTitle => !string.IsNullOrEmpty(Title) ? Title : GameId;

    public string DisplaySize => Shell.Formatting.FormatSize(Size);

    /// <summary>
    /// Returns the ground-truth launch/mount capability for this backup based on its
    /// platform and file type.
    /// </summary>
    public LaunchCapability GetLaunchCapability()
    {
        return Platform switch
        {
            GamePlatform.PS5 when FileType == BackupFileType.Folder
                => !string.IsNullOrEmpty(GameId) ? LaunchCapability.Native : LaunchCapability.InspectOnly,
            GamePlatform.PS5 when FileType == BackupFileType.Pkg
                => LaunchCapability.Installable,
            GamePlatform.PS4 when FileType == BackupFileType.Pkg
                => LaunchCapability.Installable,
            GamePlatform.PS2 when FileType is BackupFileType.Iso or BackupFileType.BinCue or BackupFileType.Folder
                => LaunchCapability.Emulatable,
            GamePlatform.PS1 when FileType is BackupFileType.Iso or BackupFileType.BinCue or BackupFileType.Folder
                => LaunchCapability.Emulatable,
            GamePlatform.PSP when FileType is BackupFileType.Iso or BackupFileType.Folder
                => LaunchCapability.Emulatable,
            _ => LaunchCapability.InspectOnly,
        };
    }

    /// <summary>
    /// Short label for the primary action button matching this backup's capability.
    /// </summary>
    public string GetLaunchLabel()
    {
        return GetLaunchCapability() switch
        {
            LaunchCapability.Native => "Launch",
            LaunchCapability.Installable => "Install",
            LaunchCapability.Emulatable => "Configure and launch",
            LaunchCapability.InspectOnly => "Inspect only",
            _ => "Inspect only",
        };
    }

    /// <summary>
    /// One-line explanation of why this capability applies to this backup.
    /// </summary>
    public string GetLaunchExplanation()
    {
        return GetLaunchCapability() switch
        {
            LaunchCapability.Native
                => "PS5 application folder, launched directly via the system launcher.",
            LaunchCapability.Installable
                => "Package file, installed via the package installer and then launched.",
            LaunchCapability.Emulatable
                => "Disc image launched through an on-device emulator.",
            LaunchCapability.InspectOnly when Platform == GamePlatform.PS2
                => "PS2 disc images cannot be launched directly yet.",
            LaunchCapability.InspectOnly when Platform == GamePlatform.PS1
                => "PS1 titles use per-title signed emulators and cannot be launched directly.",
            LaunchCapability.InspectOnly when Platform == GamePlatform.PS3
                => "No PS3 emulator available on the console.",
            LaunchCapability.InspectOnly when Platform == GamePlatform.PSP
                => "No PSP emulator available on the console.",
            LaunchCapability.InspectOnly when Platform == GamePlatform.PSVita
                => "No PS Vita emulator available on the console.",
            _ => "This backup can only be inspected.",
        };
    }

    /// <summary>
    /// Returns the region a title-id publishes under. PS4 and PS5 titles (CUSA / PPSA) share one
    /// prefix across regions, so the region for those two comes from the content id's first
    /// character instead of the prefix.
    /// </summary>
    /// <remarks>
    /// The prefix-to-region mapping is one shared table with the platform classifier so the two
    /// derivations cannot drift.
    /// </remarks>
    public static string DetectRegion(string gameId, string contentId = "")
    {
        if (string.IsNullOrEmpty(gameId) || gameId.Length < 4)
            return "Unknown";

        string region = PlatformClassifier.RegionFromPrefix(gameId);
        if (!string.IsNullOrEmpty(region))
            return region;

        if (PlatformClassifier.NeedsContentIdForRegion(gameId))
            return DetectRegionFromContentId(contentId);

        return "Unknown";
    }

    private static string DetectRegionFromContentId(string contentId)
    {
        if (string.IsNullOrEmpty(contentId))
            return "Unknown";
        return contentId[0] switch
        {
            'U' => "Americas",
            'E' => "Europe",
            'J' => "Japan",
            'H' => "Asia",
            _ => "Unknown",
        };
    }

    public static string MapPS3Category(string code)
    {
        return code switch
        {
            "DG" => "Disc Game",
            "HG" => "HDD Game",
            "GD" => "Game Data",
            "SD" => "Save Data",
            "MS" => "Memory Stick Save",
            "AT" => "Add-on Theme",
            "CB" => "App Package",
            "AP" => "Application Patch",
            _ => code,
        };
    }

    public static string MapPS4Category(string code)
    {
        return code switch
        {
            "ac" => "Additional Content",
            "bd" => "Blu-ray Disc",
            "gc" => "Game Content",
            "gd" => "Game Digital Application",
            "gda" => "System Application",
            "gdb" => "Background Application",
            "gdc" => "Mini Application",
            "gdd" => "Big Application",
            "gde" => "Video Service Application",
            "gdg" => "Game Application Patch",
            "gdgd" => "Game Application Remaster",
            "gdk" => "Video Service Native Application",
            "gdl" => "Live Area",
            "gd0" => "Pre-order Content",
            "gp" => "Game Patch",
            _ => code,
        };
    }

    public static string MapPSPCategory(string code)
    {
        return code switch
        {
            "UG" => "UMD Disc Game",
            "MG" => "Memory Stick Game",
            "MS" => "Memory Stick Save",
            "EG" => "Remaster Game",
            _ => code,
        };
    }

    public static string MapPSVCategory(string code)
    {
        return code switch
        {
            "gd" => "Game Digital Application",
            "gp" => "Game Patch",
            "ac" => "Additional Content",
            "gpc" => "Game Package Content",
            _ => code,
        };
    }
}

/// <summary>
/// Maps a title-id prefix to the platform that publishes under it, and (as the same table) to
/// the region that prefix belongs to. Both look-ups draw from one prefix table so a change to a
/// prefix's platform or region row updates both derivations at once.
/// </summary>
/// <remarks>
/// PS1 and PS2 share nine disc-serial prefixes (SLES / SCES / SLUS / SCUS / SLPS / SLPM / SCPS /
/// SCPM / SCED). The title-id-only look-up cannot separate them and answers PS1 for all nine;
/// a PS2 disc lands on the PS2 value only after DiscInspector reads the on-disc signals
/// (SYSTEM.CNF's BOOT2 line, 2048-byte-sector layout) and promotes the platform value.
/// </remarks>
internal static class PlatformClassifier
{
    // Shared prefix table for title-id-driven platform and region classification. PS4 / PS5 rows
    // carry no region because their prefix (CUSA / PPSA) is shared across regions - callers
    // derive the region from the content id instead.
    private static readonly (string Prefix, GamePlatform Platform, string? Region)[] PrefixTable = new (string, GamePlatform, string?)[]
    {
        // PS3 disc and PSN prefixes.
        ("BLES", GamePlatform.PS3, "Europe"),
        ("BCES", GamePlatform.PS3, "Europe"),
        ("BLUS", GamePlatform.PS3, "USA"),
        ("BCUS", GamePlatform.PS3, "USA"),
        ("BLJS", GamePlatform.PS3, "Japan"),
        ("BCJS", GamePlatform.PS3, "Japan"),
        ("BLAS", GamePlatform.PS3, "Asia"),
        ("BCAS", GamePlatform.PS3, "Asia"),
        ("NPEB", GamePlatform.PS3, "Europe"),
        ("NPUB", GamePlatform.PS3, "USA"),
        ("NPJB", GamePlatform.PS3, "Japan"),
        ("NPHB", GamePlatform.PS3, "Asia"),
        ("NPIB", GamePlatform.PS3, null),
        ("NPKB", GamePlatform.PS3, null),

        // PSP UMD and PSN prefixes.
        ("UCUS", GamePlatform.PSP, "USA"),
        ("ULUS", GamePlatform.PSP, "USA"),
        ("UCES", GamePlatform.PSP, "Europe"),
        ("ULES", GamePlatform.PSP, "Europe"),
        ("ULJM", GamePlatform.PSP, "Japan"),
        ("UCJS", GamePlatform.PSP, "Japan"),
        ("ULJS", GamePlatform.PSP, "Japan"),
        ("NPEG", GamePlatform.PSP, "Europe"),
        ("NPUG", GamePlatform.PSP, "USA"),
        ("NPJG", GamePlatform.PSP, "Japan"),
        ("NPHG", GamePlatform.PSP, "Asia"),
        ("NPUH", GamePlatform.PSP, "USA"),
        ("NPEH", GamePlatform.PSP, "Europe"),

        // PS Vita prefixes.
        ("PCSA", GamePlatform.PSVita, "USA"),
        ("PCSB", GamePlatform.PSVita, "Europe"),
        ("PCSC", GamePlatform.PSVita, "Japan"),
        ("PCSE", GamePlatform.PSVita, "USA"),
        ("PCSF", GamePlatform.PSVita, "Europe"),
        ("PCSG", GamePlatform.PSVita, "Japan"),

        // PS4 and PS5 use one prefix each; the content-id first character carries the region.
        ("CUSA", GamePlatform.PS4, null),
        ("PPSA", GamePlatform.PS5, null),

        // PS1 disc-serial prefixes. The nine PS2-shared prefixes below (SLES / SCES / SLUS /
        // SCUS / SLPS / SLPM / SCPS / SCPM / SCED) default to PS1 here; DiscInspector promotes
        // a disc to PS2 when it reads a PS2 SYSTEM.CNF.
        ("SLES", GamePlatform.PS1, "Europe"),
        ("SCES", GamePlatform.PS1, "Europe"),
        ("SLUS", GamePlatform.PS1, "USA"),
        ("SCUS", GamePlatform.PS1, "USA"),
        ("SLPS", GamePlatform.PS1, "Japan"),
        ("SLPM", GamePlatform.PS1, "Japan"),
        ("SCPS", GamePlatform.PS1, "Japan"),
        ("SCPM", GamePlatform.PS1, "Japan"),
        ("SCED", GamePlatform.PS1, "Europe"),
        ("PBPX", GamePlatform.PS1, "Japan"),
        ("PAPX", GamePlatform.PS1, "Japan"),
        ("PCPX", GamePlatform.PS1, "Japan"),
        ("PDPX", GamePlatform.PS1, "Japan"),
        ("PEPX", GamePlatform.PS1, "Japan"),
        ("PGPX", GamePlatform.PS1, "Japan"),
        ("PHPX", GamePlatform.PS1, "Japan"),
        ("PIPX", GamePlatform.PS1, "Japan"),
        ("PKPX", GamePlatform.PS1, "Japan"),
        ("PLPX", GamePlatform.PS1, "Japan"),
        ("SLKA", GamePlatform.PS1, "Asia"),

        // PS2-only serial prefixes (the shared PS1 / PS2 prefixes are above, keyed to PS1).
        ("SCAJ", GamePlatform.PS2, "Japan"),
        ("SCKA", GamePlatform.PS2, "Asia"),
    };

    // Region-only extras for prefixes the platform table does not carry: PSP-mini zone codes
    // (NPEZ / NPUZ / NPHZ), PS3 Asia / Korea variants (BLKS / BCKS / ECAS / ELAS), and PSP
    // Asia / Japan variants (UCPS / ELJM / UCJP). A DetectRegion caller looks these up when the
    // shared table has no row for the prefix.
    private static readonly Dictionary<string, string> RegionOnlyExtras = new(StringComparer.Ordinal)
    {
        { "NPEZ", "Europe" },
        { "NPUZ", "USA"    },
        { "NPHZ", "Asia"   },
        { "BLKS", "Asia"   },
        { "BCKS", "Asia"   },
        { "ECAS", "Asia"   },
        { "ELAS", "Asia"   },
        { "UCPS", "Asia"   },
        { "ELJM", "Japan"  },
        { "UCJP", "Japan"  },
    };

    /// <summary>
    /// Returns the platform whose prefix set carries <paramref name="titleId"/>. Empty and
    /// unrecognised ids answer <see cref="GamePlatform.Unknown"/>. The look-up trims leading and
    /// trailing whitespace and hyphens, upper-cases the id, then matches on the first four
    /// characters.
    /// </summary>
    /// <remarks>
    /// PS1 and PS2 share nine disc-serial prefixes. This look-up returns <see cref="GamePlatform.PS1"/>
    /// for every one of them; PS2 detection needs on-disc signals (SYSTEM.CNF's BOOT2 line, sector
    /// layout) that <c>DiscInspector</c> reads and applies.
    /// </remarks>
    public static GamePlatform FromTitleId(string? titleId)
    {
        string prefix = ExtractPrefix(titleId);
        if (prefix.Length < 4)
            return GamePlatform.Unknown;
        foreach (var (p, platform, _) in PrefixTable)
        {
            if (p == prefix)
                return platform;
        }
        return GamePlatform.Unknown;
    }

    /// <summary>
    /// Returns the region the given title-id prefix publishes under, or an empty string when the
    /// prefix is unknown or is a shared one whose region lives in the content id
    /// (<see cref="NeedsContentIdForRegion(string?)"/>). Trims leading and trailing whitespace and
    /// hyphens and upper-cases the input.
    /// </summary>
    public static string RegionFromPrefix(string? prefix)
    {
        string key = ExtractPrefix(prefix);
        if (key.Length < 4)
            return string.Empty;

        foreach (var (p, _, region) in PrefixTable)
        {
            if (p == key)
                return region ?? string.Empty;
        }

        return RegionOnlyExtras.TryGetValue(key, out string? extra) ? extra : string.Empty;
    }

    /// <summary>
    /// True when the title-id prefix has no region field of its own and the caller must derive
    /// the region from a content id (PS4 <c>CUSA</c> and PS5 <c>PPSA</c>).
    /// </summary>
    public static bool NeedsContentIdForRegion(string? titleId)
    {
        string prefix = ExtractPrefix(titleId);
        if (prefix.Length < 4)
            return false;
        return prefix == "CUSA" || prefix == "PPSA";
    }

    // Normalises a title id to its four-character upper-case prefix. Whitespace and hyphens are
    // stripped from both ends so a filename that carries "SLES-01234" answers the same as
    // "SLES01234".
    private static string ExtractPrefix(string? titleId)
    {
        if (string.IsNullOrEmpty(titleId))
            return string.Empty;
        string trimmed = titleId.Trim().Trim('-').ToUpperInvariant();
        if (trimmed.Length < 4)
            return string.Empty;
        return trimmed.Substring(0, 4);
    }
}
