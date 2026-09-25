// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Shell;
using SharpProspero.Interop;
using SharpProspero.Storage;
using SharpProspero.Storage.Sfo;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace ProsperoMultiTools.Data;

internal enum PkgType
{
    Unknown,
    PS3,
    PSP,
    PSVita,
    PsPsm,
    PsClassic,
    PS4,
    PS5Meta,
    PS5Retail,
    PS5Debug,
}

internal sealed class PkgInfo
{
    public PkgType Type { get; set; }
    public string FilePath { get; set; } = "";
    public string ContentId { get; set; } = "";
    public string TitleId { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>
    /// PARAM.SFO CATEGORY field (e.g. <c>HG</c> for HDD game, <c>DG</c> for disc game,
    /// <c>MG</c> for a PSP Memory-Stick install). Empty when the package's PARAM.SFO
    /// is not extractable, or when the family (PS Vita, PS5) uses a metadata source
    /// other than PARAM.SFO.
    /// </summary>
    public string Category { get; set; } = "";

    /// <summary>
    /// PARAM.SFO <c>APP_VER</c> (falling back to <c>VERSION</c>). Empty when the field
    /// is missing or when the package does not carry an extractable PARAM.SFO.
    /// </summary>
    public string AppVersion { get; set; } = "";

    /// <summary>
    /// On-disk path to a cached ICON0.PNG the reader extracted from the package's
    /// encrypted region. Empty when no icon was extractable or when the cover cache
    /// folder is not reachable at read time.
    /// </summary>
    public string IconPath { get; set; } = "";

    public long FileSize { get; set; }
    public ParamJson? Param { get; set; }
}

internal static class PkgReader
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // The two container magics used across the entire PlayStation package family. NPDRM packages
    // for PS3, PSP, PS Vita, PS1-classic and PSM all use "\x7FPKG". Both PS4 and PS5 use "\x7FCNT"
    // for the outer container; PS5 also uses "\x7FFIH" for finalized retail and debug packages
    // that embed a CNT container at an offset stored in the FIH header.
    private const uint MagicPkg = 0x7F504B47; // "\x7FPKG"
    private const uint MagicCnt = 0x7F434E54; // "\x7FCNT"
    private const uint MagicFih = 0x7F464948; // "\x7FFIH"

    private const int HeaderReadSize = 512;
    private const int MaxMetadataSize = 16 * 1024 * 1024;

    // NPDRM header field layout (big-endian throughout).
    private const int NpdrmPkgRevisionField = 0x04;
    private const int NpdrmPkgTypeField = 0x06;
    private const int NpdrmMetaOffsetField = 0x08;
    private const int NpdrmMetaCountField = 0x0C;
    private const int NpdrmContentIdField = 0x30;
    private const int NpdrmContentIdSize = 0x30;

    // pkg_revision at 0x04 identifies the target platform when pkg_type alone leaves it ambiguous.
    // The value 0x8000 is the Vita marker seen on every retail Vita package; older PSP releases
    // ship 0x0000 or 0x0080. When the metadata-table content_type entry is missing or unknown, the
    // revision word is the reliable tie-breaker between PSP and Vita.
    private const ushort NpdrmPkgRevisionVita = 0x8000;

    // CNT header field layout (big-endian). content_type at 0x74 distinguishes PS4 from PS5 Meta.
    private const int CntContentIdOffset = 0x40;
    private const int CntContentIdSize = 0x30;
    private const int CntContentTypeOffset = 0x74;

    // FIH header field layout (little-endian). Byte 5 is the signed-image marker; byte 6..7
    // is the format version. 0x58 stores the embedded CNT container's absolute file offset.
    private const int FihSignedByteOffset = 0x05;
    private const int FihFormatVersionField = 0x06;
    private const ushort FihRequiredFormatVersion = 3;
    private const int FihEmbeddedCntOffsetField = 0x58;
    private const int FihMinHeaderSize = FihEmbeddedCntOffsetField + 8;

    // NPDRM content_type values seen across the PS3/PSP/Vita/PSX/PSM family. The single-byte
    // value alone is the identifier the on-console install pipeline keys the platform on. The
    // pkg_type + pkg_revision header fields serve only as a fallback for a package whose
    // metadata table is missing or unreadable.
    private const uint ContentTypePsx = 0x01;
    private const uint ContentTypePs3Game = 0x04;
    private const uint ContentTypePs3Update = 0x05;
    private const uint ContentTypePs3Standalone = 0x06;
    private const uint ContentTypePspBase = 0x07;
    private const uint ContentTypePs3Reserved = 0x08;
    private const uint ContentTypePs3Dlc = 0x09;
    private const uint ContentTypePs3Patch = 0x0A;
    private const uint ContentTypePs3Theme = 0x0B;
    private const uint ContentTypePs3Widget = 0x0C;
    private const uint ContentTypePs3License2 = 0x0D;
    private const uint ContentTypePspEmu = 0x0E;
    private const uint ContentTypePspMinis = 0x0F;
    private const uint ContentTypePs3Avatar = 0x10;
    private const uint ContentTypePs3AppInstaller = 0x11;
    private const uint ContentTypePspGo = 0x12;
    private const uint ContentTypePspMinisAlt = 0x13;
    // 0x14 is a PS3 runtime that plays remastered PSP titles - the package format
    // and install path are PS3, so it surfaces under the PS3 browser rather than PSP.
    private const uint ContentTypePs3PspRemaster = 0x14;
    private const uint ContentTypeVitaApp = 0x15;
    private const uint ContentTypeVitaDlc = 0x16;
    private const uint ContentTypeVitaPatch = 0x17;
    private const uint ContentTypePsmSdk = 0x18;
    // 0x19..0x1C sit between PSM's SDK marker (0x18) and its Unity marker (0x1D) and
    // ship variant PSM packages that run on PS Vita; they belong in the PS Vita
    // browser rather than in a PSM-only bucket a user would not see.
    private const uint ContentTypeVitaPsm19 = 0x19;
    private const uint ContentTypeVitaPsm1A = 0x1A;
    private const uint ContentTypeVitaPsm1B = 0x1B;
    private const uint ContentTypeVitaPsm1C = 0x1C;
    private const uint ContentTypePsmUnity = 0x1D;
    private const uint ContentTypePs3NeoGeo = 0x1E;
    private const uint ContentTypeVitaTheme = 0x1F;
    // 0x20 in the NPDRM (PKG magic) table is a Vita PocketStation package. The same
    // value in a CNT header is the PS5 GD marker, but a value in the CNT table is
    // never reached from this NPDRM mapping path.
    private const uint ContentTypeVitaPocketStation = 0x20;

    // PS4 CNT content_type values.
    private const uint Ps4ContentTypeGd = 0x1A;
    private const uint Ps4ContentTypeAc = 0x1B;
    private const uint Ps4ContentTypeAl = 0x1C;
    private const uint Ps4ContentTypeDp = 0x1E;

    // PS5 CNT content_type values as the local package builder writes them into the embedded
    // CNT header at 0x74. A bare CNT container carrying one of these is a PS5 metadata-only
    // bundle (whole PS5 packages are wrapped in a FIH container that embeds a CNT, and the
    // outer FIH magic is what a real PS5 package on disc starts with; a stand-alone CNT with a
    // PS5 content_type is either a fragment or a metadata-only bundle from a build-tool).
    private const uint Ps5ContentTypeGd = 0x20;
    private const uint Ps5ContentTypeAc = 0x21;
    private const uint Ps5ContentTypeAl = 0x22;

    /// <summary>
    /// Detects the package family from a header buffer of at least <see cref="HeaderReadSize"/>
    /// bytes. Returns <see cref="PkgType.Unknown"/> when the magic is not recognised.
    /// </summary>
    public static PkgType DetectType(byte[] header)
    {
        if (header.Length < 8)
            return PkgType.Unknown;

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));

        if (magic == MagicFih)
        {
            // Only FIH format version 3 is a real PS5 package; earlier or later versions carry
            // a different field layout and would read wrong data if fed to the embedded-CNT
            // offset reader below. Refuse them explicitly rather than routing them into the
            // PS5 pipeline.
            if (header.Length < FihFormatVersionField + 2)
                return PkgType.Unknown;
            ushort formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(
                header.AsSpan(FihFormatVersionField, 2));
            if (formatVersion != FihRequiredFormatVersion)
                return PkgType.Unknown;

            byte signedByte = header[FihSignedByteOffset];
            return signedByte switch
            {
                0x80 => PkgType.PS5Retail,
                0x00 => PkgType.PS5Debug,
                _ => PkgType.Unknown,
            };
        }

        if (magic == MagicCnt)
        {
            // PS4 and PS5 both use the CNT container. content_type at 0x74 discriminates: a
            // PS4-family type (0x1A..0x1E) maps to PS4, a PS5-family type (0x20..0x22) maps
            // to a PS5 metadata bundle. content_type values 0x20 and above that are not in
            // the currently-known PS5 set are routed to PS5Meta so a future PS5 variant that
            // adds another content_type surfaces in the PS5 tab rather than being classed
            // as PS4. content_type below 0x20 that is not in the PS4 set is routed to PS4
            // because the PS4 family is open-ended (patches, themes, remasters) - but every
            // PS4 sub-family observed in real corpuses carries one of the four known values,
            // so the sub-0x20 fallback is a defense-in-depth branch rather than a normal path.
            if (header.Length >= CntContentTypeOffset + 4)
            {
                uint contentType = BinaryPrimitives.ReadUInt32BigEndian(
                    header.AsSpan(CntContentTypeOffset, 4));
                return contentType switch
                {
                    Ps4ContentTypeGd or Ps4ContentTypeAc or Ps4ContentTypeAl or Ps4ContentTypeDp
                        => PkgType.PS4,
                    Ps5ContentTypeGd or Ps5ContentTypeAc or Ps5ContentTypeAl
                        => PkgType.PS5Meta,
                    _ => contentType >= Ps5ContentTypeGd ? PkgType.PS5Meta : PkgType.PS4,
                };
            }
            return PkgType.PS4;
        }

        if (magic == MagicPkg)
        {
            // NPDRM package. pkg_type at offset 6 (BE u16) marks the era: 1 = PS3, 2 = PSP / Vita
            // / PSX / PSM. pkg_revision at offset 4 further distinguishes retail Vita (0x8000) from
            // older PSP releases (0x0000 / 0x0080). The metadata-table content_type entry, when
            // present, wins over both; this initial classification only stands when the table walk
            // does not find a type-2 entry or the value it finds is unknown.
            ushort pkgType = BinaryPrimitives.ReadUInt16BigEndian(
                header.AsSpan(NpdrmPkgTypeField, 2));
            ushort pkgRevision = BinaryPrimitives.ReadUInt16BigEndian(
                header.AsSpan(NpdrmPkgRevisionField, 2));
            if (pkgType == 1)
                return PkgType.PS3;
            // pkg_type=2 with the Vita revision marker starts out as Vita; every other pkg_type=2
            // starts out as PSP. This gets a Vita package with an absent or unrecognised
            // content_type into the PS Vita browser instead of the PSP one.
            if (pkgType == 2 && pkgRevision == NpdrmPkgRevisionVita)
                return PkgType.PSVita;
            return PkgType.PSP;
        }

        return PkgType.Unknown;
    }

    /// <summary>
    /// Reads the on-disk header, detects the package family, and fills in the derived
    /// title metadata. Returns <see langword="null"/> when the file is not a recognisable
    /// package or is too short. Paths on a partition the module's mount namespace does not
    /// bind (a game backup landing under <c>/data</c>, a metadata file under <c>/user</c>)
    /// are read through the escalation daemon; paths on a bound partition open directly.
    /// </summary>
    public static PkgInfo? ReadPkgInfo(string path)
    {
        using RandomAccessByteSource? source = RandomAccessByteSource.Open(path);
        if (source is null)
            return null;

        long fileSize = source.Size;
        if (fileSize < HeaderReadSize)
            return null;

        byte[] header = new byte[HeaderReadSize];
        int headerRead = 0;
        while (headerRead < HeaderReadSize)
        {
            int n = source.ReadAt(headerRead, header.AsSpan(headerRead));
            if (n <= 0)
                return null;
            headerRead += n;
        }

        PkgType type = DetectType(header);
        if (type == PkgType.Unknown)
            return null;

        var info = new PkgInfo
        {
            Type = type,
            FilePath = path,
            FileSize = fileSize,
        };

        switch (type)
        {
            case PkgType.PS5Retail:
            case PkgType.PS5Debug:
            case PkgType.PS5Meta:
                ReadPs5Metadata(source, fileSize, type, info);
                break;
            case PkgType.PS4:
                ReadPs4Metadata(header, info);
                break;
            case PkgType.PS3:
            case PkgType.PSP:
            case PkgType.PSVita:
                // Read the metadata table content_type entry to narrow the family. Every NPDRM
                // package family (PS3, PSP, Vita, PSX, PSM) writes a content_type value into the
                // metadata table and the on-console install pipeline reads the last one written,
                // so a walk on every NPDRM package pulls the right platform out of the header
                // even when the pkg_type + pkg_revision fields alone would misclassify it.
                RefineNpdrmType(source, header, info);
                ReadNpdrmContentId(header, info);

                // PS3, PSP and PS Vita retail packages all carry a PARAM.SFO and ICON0.PNG the
                // browser needs. PS3 and PSP hold both inside the AES-128-CTR encrypted file
                // table under a single retail key per family. PS Vita retail packages hold the
                // PARAM.SFO plaintext outside the encrypted region (via metadata packet type
                // 14) and hold the ICON0.PNG inside the encrypted table under a per-package
                // session key derived from the extended-header pkg_key_id (types 2 for
                // application packages, 3 for LiveArea), so the Vita path takes the plaintext
                // SFO route first and only walks the encrypted table for the icon.
                if (info.Type is PkgType.PS3 or PkgType.PSP)
                    PopulateFromEncryptedRegion(source, info);
                else if (info.Type == PkgType.PSVita)
                    PopulateVitaFromMetadata(source, info);

                // Belt-and-suspenders cross-check: a content_type suggesting one family does
                // not stand when the TITLE_ID prefix unambiguously names a different one. This
                // catches a mislabeled package (e.g. a debug tool that wrote the wrong
                // content_type into the metadata table) and drops it to Unknown so the browser
                // does not surface it under the wrong platform tab. PsPsm is left alone because
                // PSM shares its prefix set with PSVita, and PsClassic is left alone because
                // classic NPDRM ids overlap PS3 NP-prefixed ids.
                if (!string.IsNullOrEmpty(info.TitleId)
                    && info.Type is not PkgType.PsPsm and not PkgType.PsClassic)
                {
                    PkgType implied = PlatformFromTitleIdPrefix(info.TitleId);
                    if (implied != PkgType.Unknown && implied != info.Type)
                        return null;
                }
                break;
        }

        return info;
    }

    private static void ReadPs4Metadata(byte[] header, PkgInfo info)
    {
        if (header.Length < CntContentIdOffset + CntContentIdSize)
            return;
        int cidLen = 0;
        while (cidLen < CntContentIdSize && header[CntContentIdOffset + cidLen] != 0)
            cidLen++;
        if (cidLen == 0)
            return;
        info.ContentId = Encoding.ASCII.GetString(header, CntContentIdOffset, cidLen);
        if (info.ContentId.Length >= 16)
            info.TitleId = info.ContentId.Substring(7, 9);
    }

    private static void ReadNpdrmContentId(byte[] header, PkgInfo info)
    {
        if (header.Length < NpdrmContentIdField + NpdrmContentIdSize)
            return;
        int cidLen = 0;
        while (cidLen < NpdrmContentIdSize && header[NpdrmContentIdField + cidLen] != 0)
            cidLen++;
        if (cidLen == 0)
            return;
        info.ContentId = Encoding.ASCII.GetString(header, NpdrmContentIdField, cidLen);
        if (info.ContentId.Length >= 16)
            info.TitleId = info.ContentId.Substring(7, 9);
    }

    // Walks the NPDRM metadata table for the content_type entry (type == 2) and maps the value
    // to the concrete platform. Silent on failure so a truncated or unusual header still yields
    // the initial header-only classification. The whole table is walked and the last type=2
    // entry wins; some packages carry more than one and the on-console install pipeline reads
    // the last one written into the header.
    private static void RefineNpdrmType(RandomAccessByteSource source, byte[] header, PkgInfo info)
    {
        if (info.Type != PkgType.PSP && info.Type != PkgType.PS3 && info.Type != PkgType.PSVita)
            return;

        uint metaOffset = BinaryPrimitives.ReadUInt32BigEndian(
            header.AsSpan(NpdrmMetaOffsetField, 4));
        uint metaCount = BinaryPrimitives.ReadUInt32BigEndian(
            header.AsSpan(NpdrmMetaCountField, 4));
        if (metaOffset == 0 || metaCount == 0 || metaCount > 256)
            return;

        byte[] block = new byte[16];
        long pos = metaOffset;
        uint? lastContentType = null;
        for (uint i = 0; i < metaCount; i++)
        {
            int n = source.ReadAt(pos, block);
            if (n < 8)
                break;
            uint type = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(0, 4));
            uint size = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(4, 4));
            if (type == 2 && n >= 12)
                lastContentType = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(8, 4));
            pos += 8 + size;
            if (pos <= 0 || pos > info.FileSize - 8)
                break;
        }
        if (lastContentType.HasValue)
        {
            PkgType mapped = MapNpdrmContentType(lastContentType.Value, info.Type);
            if (mapped != PkgType.Unknown)
                info.Type = mapped;
        }
    }

    // Content-type value to platform. When the value is not in the table the initial header-based
    // classification is kept, so a fresh content_type does not corrupt the tab a package lands in.
    // The fall-through preserves the caller's <paramref name="fallback"/> instead of guessing a
    // platform: an unknown value on a pkg_type=1 header stays PS3, an unknown value on a
    // pkg_type=2 + pkg_revision=Vita header stays Vita, and every other pkg_type=2 header stays
    // PSP. None of them leak into a tab their pkg_type + pkg_revision does not name.
    private static PkgType MapNpdrmContentType(uint contentType, PkgType fallback)
    {
        return contentType switch
        {
            ContentTypePsx => PkgType.PsClassic,
            ContentTypePs3Game or ContentTypePs3Update or ContentTypePs3Standalone
                or ContentTypePs3Reserved or ContentTypePs3Dlc or ContentTypePs3Patch
                or ContentTypePs3Theme or ContentTypePs3Widget or ContentTypePs3License2
                or ContentTypePs3Avatar or ContentTypePs3AppInstaller
                or ContentTypePs3PspRemaster or ContentTypePs3NeoGeo
                => PkgType.PS3,
            ContentTypePspBase or ContentTypePspEmu or ContentTypePspMinis
                or ContentTypePspGo or ContentTypePspMinisAlt
                => PkgType.PSP,
            ContentTypeVitaApp or ContentTypeVitaDlc or ContentTypeVitaPatch
                or ContentTypeVitaTheme or ContentTypeVitaPocketStation
                or ContentTypeVitaPsm19 or ContentTypeVitaPsm1A
                or ContentTypeVitaPsm1B or ContentTypeVitaPsm1C
                => PkgType.PSVita,
            ContentTypePsmSdk or ContentTypePsmUnity => PkgType.PsPsm,
            _ => fallback,
        };
    }

    // Walks the encrypted file table of a retail PS3 or PSP package, extracts PARAM.SFO and
    // ICON0.PNG, then folds their contents into <paramref name="info"/>. Fields already set
    // from the header (TitleId, ContentId) are only overwritten when the SFO carries a
    // non-empty value, so a package with a partial SFO does not lose the header data. Silent
    // on failure: a package with no readable PARAM.SFO leaves the header-derived fields
    // untouched and returns without error.
    private static void PopulateFromEncryptedRegion(RandomAccessByteSource source, PkgInfo info)
    {
        PkgFileEntry? sfoEntry = null;
        PkgFileEntry? iconEntry = null;
        foreach (PkgFileEntry entry in PkgAesCtrDecryptor.EnumerateEntries(source, info.Type))
        {
            // Directories carry file_type = 0x04 with data_size = 0 and can be skipped;
            // the browser only needs the file entries.
            if ((entry.Flags & 0xFF) == 0x04 || entry.DataSize == 0)
                continue;
            if (sfoEntry is null && IsNameEqual(entry.Name, "PARAM.SFO"))
                sfoEntry = entry;
            else if (iconEntry is null && IsNameEqual(entry.Name, "ICON0.PNG"))
                iconEntry = entry;
            if (sfoEntry is not null && iconEntry is not null)
                break;
        }

        if (sfoEntry is PkgFileEntry sfo)
        {
            byte[]? sfoBytes = PkgAesCtrDecryptor.ExtractEntry(source, sfo, info.Type);
            if (sfoBytes is not null)
            {
                SfoFile? parsed = SfoFile.Read(sfoBytes);
                if (parsed is not null)
                    ApplySfoFields(parsed, info);
            }
        }

        if (iconEntry is PkgFileEntry icon)
        {
            byte[]? iconBytes = PkgAesCtrDecryptor.ExtractEntry(source, icon, info.Type);
            if (iconBytes is not null && iconBytes.Length > 0)
                TryCacheExtractedIcon(iconBytes, info);
        }
    }

    // Fills a PS Vita or PSM PKG's title metadata + cover from the two on-disk sources
    // the Vita installer keeps for a retail package. PARAM.SFO is read directly from the
    // plaintext metadata packet (type 14) that sits outside the encrypted region, so title
    // metadata is available even when the encrypted table's per-package session key is not
    // usable (a truncated download, a hostile package). ICON0.PNG is fetched by walking the
    // encrypted file table under the derived session key; a package that omits the icon
    // simply leaves the cover cache empty. Silent on failure - a Vita package with neither
    // an SFO nor an icon keeps the header-derived TitleId/ContentId untouched. The
    // decryptor always runs its Vita key-derivation path because PSM packages ride on the
    // exact same on-disk layout as Vita application packages and only differ by the base
    // key selected by the extended-header pkg_key_id.
    private static void PopulateVitaFromMetadata(RandomAccessByteSource source, PkgInfo info)
    {
        byte[]? plaintextSfo = PkgAesCtrDecryptor.ReadVitaPlaintextSfo(source);
        if (plaintextSfo is not null)
        {
            SfoFile? parsed = SfoFile.Read(plaintextSfo);
            if (parsed is not null)
                ApplySfoFields(parsed, info);
        }

        // The Vita ICON0.PNG lives inside the encrypted file table, under a name Vita
        // packages consistently use for the retail icon. Walk the table once, stop at the
        // first match so the second-order enumeration bill (name decryption per entry)
        // stays bounded for a package with hundreds of entries. If the encrypted table
        // does not include ICON0.PNG the browser falls back to its shared cover library.
        PkgFileEntry? iconEntry = null;
        foreach (PkgFileEntry entry in PkgAesCtrDecryptor.EnumerateEntries(source, PkgType.PSVita))
        {
            if ((entry.Flags & 0xFF) == 0x04 || entry.DataSize == 0)
                continue;
            if (IsNameEqual(entry.Name, "ICON0.PNG"))
            {
                iconEntry = entry;
                break;
            }
        }

        if (iconEntry is PkgFileEntry icon)
        {
            byte[]? iconBytes = PkgAesCtrDecryptor.ExtractEntry(source, icon, PkgType.PSVita);
            if (iconBytes is not null && iconBytes.Length > 0)
                TryCacheExtractedIcon(iconBytes, info);
        }
    }

    // Compares two ASCII filenames without allocating an intermediate uppercase copy.
    // PKG entry names come out of the encrypted table as ASCII; a case-insensitive
    // compare here lets an all-lowercase "param.sfo" from an older publish still match.
    private static bool IsNameEqual(string a, string b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            char ca = a[i];
            char cb = b[i];
            if (ca >= 'a' && ca <= 'z') ca = (char)(ca - 32);
            if (cb >= 'a' && cb <= 'z') cb = (char)(cb - 32);
            if (ca != cb)
                return false;
        }
        return true;
    }

    // Folds the SFO fields the browser cares about into the package info. Whitespace and
    // trailing NUL bytes get trimmed off, and internal runs of whitespace collapse to a
    // single space so a title like "GAME\0\0" or "  GAME  " renders cleanly. Only non-empty
    // SFO values overwrite existing header-derived fields.
    private static void ApplySfoFields(SfoFile sfo, PkgInfo info)
    {
        string title = NormalizeSfoText(sfo.GetString("TITLE"));
        if (title.Length > 0)
            info.Title = title;

        string titleId = NormalizeSfoText(sfo.GetString("TITLE_ID"));
        if (titleId.Length > 0)
            info.TitleId = titleId;

        string contentId = NormalizeSfoText(sfo.GetString("CONTENT_ID"));
        if (contentId.Length > 0)
            info.ContentId = contentId;

        string category = NormalizeSfoText(sfo.GetString("CATEGORY"));
        if (category.Length > 0)
            info.Category = category;

        string appVer = NormalizeSfoText(sfo.GetString("APP_VER"));
        if (appVer.Length == 0)
            appVer = NormalizeSfoText(sfo.GetString("VERSION"));
        if (appVer.Length > 0)
            info.AppVersion = appVer;
    }

    // Trims trailing NUL and whitespace off the SFO value and collapses runs of internal
    // whitespace to a single space. Returns the empty string when the input is null.
    private static string NormalizeSfoText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        int start = 0;
        int end = value.Length;
        while (end > start && (value[end - 1] == '\0' || char.IsWhiteSpace(value[end - 1])))
            end--;
        while (start < end && char.IsWhiteSpace(value[start]))
            start++;
        if (start >= end)
            return string.Empty;

        var sb = new StringBuilder(end - start);
        bool lastWasSpace = false;
        for (int i = start; i < end; i++)
        {
            char c = value[i];
            if (c == '\0')
                continue;
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }
        return sb.ToString();
    }

    // Writes the extracted ICON0.PNG to the shared cover cache under the platform folder
    // the browser looks in, using the same "TITLE_ID uppercase, hyphens removed, .png"
    // naming the ISO icon extractor already uses. Broker-aware: the cache folder often
    // lives on /data, where the module's mount namespace does not bind directly and the
    // write has to route through the escalation daemon. Silent on failure - the browser
    // falls back to the shared cover library when the cache write does not land.
    private static void TryCacheExtractedIcon(byte[] iconBytes, PkgInfo info)
    {
        if (string.IsNullOrEmpty(info.TitleId))
            return;
        string? cacheFolder = CoverService.CacheFolder();
        if (cacheFolder is null)
            return;

        string subfolder = info.Type switch
        {
            PkgType.PS3 => "PS3",
            PkgType.PSP => "PSP",
            PkgType.PSVita => "PSVita",
            _ => string.Empty,
        };
        if (subfolder.Length == 0)
            return;

        string platformFolder = PathUtil.Combine(cacheFolder, subfolder);
        if (!EnsureCacheDirectory(platformFolder))
            return;

        string key = NormalizeCoverKey(info.TitleId);
        if (key.Length == 0)
            return;

        string iconPath = PathUtil.Combine(platformFolder, key + ".png");
        if (!WriteCoverBytes(iconPath, iconBytes))
            return;
        info.IconPath = iconPath;
    }

    // Uppercases the TitleId and strips hyphens so the cached filename matches the
    // key CoverService already uses for its ISO-extracted icon cache. A blank input
    // returns an empty string, which the caller treats as no-cache-key.
    private static string NormalizeCoverKey(string titleId)
    {
        string trimmed = titleId.Trim();
        if (trimmed.Length == 0)
            return string.Empty;
        var sb = new StringBuilder(trimmed.Length);
        foreach (char c in trimmed)
        {
            if (c == '-')
                continue;
            sb.Append(c >= 'a' && c <= 'z' ? (char)(c - 32) : c);
        }
        return sb.ToString();
    }

    // Creates the target folder, routing through the escalation daemon when it lives on a
    // partition the module's own mount namespace does not bind. Returns true when the
    // folder exists at the end of the call, whether it was created here or was already
    // present.
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

    // Writes bytes to disk, broker-aware. Returns true on success.
    private static bool WriteCoverBytes(string path, byte[] bytes)
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

    // Maps a TITLE_ID prefix to the platform it can only belong to. Returns
    // <see cref="PkgType.Unknown"/> for a prefix that spans multiple platforms
    // (every NP-prefixed NPDRM id, PSM's ids on PS Vita, or an unknown prefix
    // shape) so the cross-check does not demote a legitimately-tagged package
    // over an ambiguous id.
    private static PkgType PlatformFromTitleIdPrefix(string titleId)
    {
        if (titleId.Length < 4)
            return PkgType.Unknown;
        // Uppercase the first four characters in place.
        Span<char> prefixChars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            char c = titleId[i];
            prefixChars[i] = (c >= 'a' && c <= 'z') ? (char)(c - 32) : c;
        }
        string prefix = new(prefixChars);

        return prefix switch
        {
            // Disc-media PS3 codes.
            "SLES" or "SLUS" or "SLPS" or "SLPM"
                or "SCES" or "SCUS" or "SCPS"
                or "BLES" or "BLUS" or "BLJS" or "BLJM" or "BLKS"
                or "BCES" or "BCUS" or "BCJS" or "BCJM" or "BCKS"
                or "MRTC" or "MTPS"
                => PkgType.PS3,

            // UMD and PSP-download codes.
            "UCES" or "UCUS" or "UCJS" or "UCJP" or "UCKS" or "UCAS" or "UCPS"
                or "ULES" or "ULUS" or "ULJM" or "ULJS" or "ULKS" or "ULAS" or "ULAM"
                or "ELJM" or "ELAS"
                => PkgType.PSP,

            // PS Vita codes. Retail cartridges and NPDRM downloads share the "PCS"
            // and "VC*" families; both live in the PSVita browser.
            "PCSA" or "PCSB" or "PCSC" or "PCSD" or "PCSE" or "PCSF" or "PCSG" or "PCSH"
                or "VCJS" or "VCJP" or "VCUS" or "VCES" or "VLES" or "VLUS"
                => PkgType.PSVita,

            _ => PkgType.Unknown,
        };
    }

    private static void ReadPs5Metadata(RandomAccessByteSource source, long fileSize, PkgType type, PkgInfo info)
    {
        long metadataOffset = 0;

        if (type is PkgType.PS5Retail or PkgType.PS5Debug)
        {
            if (fileSize < FihMinHeaderSize)
                return;

            byte[] fihBuf = new byte[FihMinHeaderSize];
            int fihRead = 0;
            while (fihRead < FihMinHeaderSize)
            {
                int n = source.ReadAt(fihRead, fihBuf.AsSpan(fihRead));
                if (n <= 0)
                    return;
                fihRead += n;
            }

            metadataOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(
                fihBuf.AsSpan(FihEmbeddedCntOffsetField));

            if (metadataOffset <= 0 || metadataOffset + 64 > fileSize)
                return;

            byte[] magic = new byte[4];
            if (source.ReadAt(metadataOffset, magic) < 4)
                return;
            if (magic[0] != 0x7F || magic[1] != (byte)'C' ||
                magic[2] != (byte)'N' || magic[3] != (byte)'T')
                return;
        }

        long available = fileSize - metadataOffset;
        int readSize = (int)Math.Min(available, MaxMetadataSize);
        byte[] data = new byte[readSize];
        int totalRead = 0;
        while (totalRead < readSize)
        {
            int n = source.ReadAt(metadataOffset + totalRead, data.AsSpan(totalRead));
            if (n <= 0)
                break;
            totalRead += n;
        }

        if (totalRead < 64)
            return;

        if (totalRead >= CntContentIdOffset + CntContentIdSize)
        {
            int cidLen = 0;
            while (cidLen < CntContentIdSize && data[CntContentIdOffset + cidLen] != 0)
                cidLen++;
            if (cidLen > 0)
            {
                info.ContentId = Encoding.ASCII.GetString(data, CntContentIdOffset, cidLen);
                if (info.ContentId.Length >= 16)
                    info.TitleId = info.ContentId.Substring(7, 9);
            }
        }

        byte[]? paramBytes = type == PkgType.PS5Debug
            ? ExtractDebugParamJson(data, 0, totalRead)
            : ExtractRetailParamJson(data, 0, totalRead);

        if (paramBytes is not null)
        {
            info.Param = ParamJson.ReadFromBytes(paramBytes);
            if (info.Param is not null)
            {
                if (string.IsNullOrEmpty(info.TitleId))
                    info.TitleId = info.Param.TitleId;
                if (string.IsNullOrEmpty(info.ContentId))
                    info.ContentId = info.Param.ContentId;
                info.Title = info.Param.DisplayTitle;
            }
        }
    }

    public static byte[]? ExtractRetailParamJson(byte[] data, int offset, int length)
    {
        byte[] jsonMarker = Encoding.ASCII.GetBytes("param.json");
        int pos = FindBytes(data, jsonMarker, offset, length);
        if (pos < 0)
            return null;

        int searchStart = pos + jsonMarker.Length;
        byte[] openBrace = [(byte)'{'];
        int jsonStart = FindBytes(data, openBrace, searchStart, length);
        if (jsonStart < 0)
            return null;

        return ExtractJsonObject(data, jsonStart, length);
    }

    public static byte[]? ExtractDebugParamJson(byte[] data, int offset, int length)
    {
        byte[] xmlMarker = Encoding.ASCII.GetBytes("package-configuration");
        int xmlPos = FindBytes(data, xmlMarker, offset, length);
        if (xmlPos < 0)
            return ExtractRetailParamJson(data, offset, length);

        byte[] paramMarker = Encoding.ASCII.GetBytes("param.json");
        int paramPos = FindBytes(data, paramMarker, xmlPos, length);
        if (paramPos < 0)
            return null;

        byte[] openBrace = [(byte)'{'];
        int jsonStart = FindBytes(data, openBrace, paramPos, length);
        if (jsonStart < 0)
            return null;

        return ExtractJsonObject(data, jsonStart, length);
    }

    private static byte[]? ExtractJsonObject(byte[] data, int jsonStart, int limit)
    {
        int end = Math.Min(data.Length, limit);
        int depth = 0;
        int jsonEnd = -1;
        bool inString = false;

        for (int i = jsonStart; i < end; i++)
        {
            byte b = data[i];

            if (inString)
            {
                if (b == (byte)'\\')
                {
                    // Skip the escaped character so a \" does not end the string
                    // and a \\ does not leave the next character misinterpreted.
                    i++;
                }
                else if (b == (byte)'"')
                {
                    inString = false;
                }
                continue;
            }

            if (b == (byte)'"')
            {
                inString = true;
            }
            else if (b == (byte)'{')
            {
                depth++;
            }
            else if (b == (byte)'}')
            {
                depth--;
                if (depth == 0)
                {
                    jsonEnd = i + 1;
                    break;
                }
            }
        }

        if (jsonEnd < 0)
            return null;

        int length = jsonEnd - jsonStart;
        byte[] result = new byte[length];
        Array.Copy(data, jsonStart, result, 0, length);
        return result;
    }

    public static byte[]? ExtractIconPng(byte[] data)
    {
        int pos = FindBytes(data, PngSignature, 0, data.Length);
        while (pos >= 0)
        {
            int end = FindPngEnd(data, pos);
            if (end > pos)
            {
                int length = end - pos;
                byte[] png = new byte[length];
                Array.Copy(data, pos, png, 0, length);
                return png;
            }
            pos = FindBytes(data, PngSignature, pos + 1, data.Length);
        }
        return null;
    }

    public static byte[]? ExtractBackgroundPng(byte[] data)
    {
        int count = 0;
        int pos = FindBytes(data, PngSignature, 0, data.Length);
        while (pos >= 0)
        {
            count++;
            int end = FindPngEnd(data, pos);
            if (count >= 2 && end > pos)
            {
                int length = end - pos;
                byte[] png = new byte[length];
                Array.Copy(data, pos, png, 0, length);
                return png;
            }
            pos = FindBytes(data, PngSignature, (end > pos ? end : pos + 1), data.Length);
        }
        return null;
    }

    private static int FindPngEnd(byte[] data, int pngStart)
    {
        byte[] iend = [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];
        int pos = FindBytes(data, iend, pngStart + 8, data.Length);
        if (pos >= 0)
            return pos + iend.Length;
        return -1;
    }

    private static int FindBytes(byte[] data, byte[] pattern, int start, int limit)
    {
        if (pattern.Length == 0 || start + pattern.Length > data.Length)
            return -1;

        int end = Math.Min(data.Length, limit) - pattern.Length;
        for (int i = start; i <= end; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }
}
