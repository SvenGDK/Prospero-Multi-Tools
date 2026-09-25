// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using SharpProspero.Security;

namespace ProsperoMultiTools.Data;

/// <summary>
/// Names one entry inside a retail PS3 or PSP NPDRM package. <see cref="DataOffset"/>
/// and <see cref="DataSize"/> are in the encrypted region's coordinate space (both are
/// byte offsets relative to the header's <c>data_offset</c> field). <see cref="Flags"/>
/// packs the raw entry metadata: the high 8 bits carry the <c>content_type</c> byte and
/// the low 8 bits carry the <c>file_type</c> byte so a caller can tell a directory
/// (file_type = 0x04) from a file without another table walk.
/// </summary>
internal readonly record struct PkgFileEntry(string Name, long DataOffset, long DataSize, uint Flags);

/// <summary>
/// AES-CTR decryption for finalized retail NPDRM packages across the PS3
/// (<c>pkg_type = 1</c>), PSP (<c>pkg_type = 2</c>, PSP key ladder) and PS Vita
/// (<c>pkg_type = 2</c>, per-package derived key ladder) families. Every entry point
/// accepts a package whose magic is <c>\x7FPKG</c> and whose finalized byte at 0x04
/// is 0x80. All three families share the main-header layout - a fixed 0xC0-byte
/// header at offset 0, followed by the encrypted region that starts at
/// <c>data_offset</c> (u64 big-endian at 0x20) and spans <c>data_size</c> bytes
/// (u64 big-endian at 0x28) - and differ in the AES-128 key that turns the
/// per-package counter seed at 0x70 into the CTR keystream:
/// <list type="bullet">
///   <item><description>PS3 and PSP use a single retail key for every package.</description></item>
///   <item><description>PS Vita, PS Vita Livearea and PSM use a per-package session key formed
///     by AES-128-ECB encrypting the counter seed with a per-key-id base key selected
///     by the extended-header field <c>pkg_key_id</c> at file offset 0xE4.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// The counter for AES-CTR is a 128-bit big-endian integer that begins at the header's
/// <c>pkg_file_key</c> and increments by one per 16-byte block. Debug packages
/// (finalized byte 0x00) are refused at header parse so a caller does not receive a
/// plausible-looking but wrong plaintext.
/// </remarks>
internal static class PkgAesCtrDecryptor
{
    /// <summary>
    /// Retail AES-128 key used to derive the CTR keystream for every finalized PS3
    /// NPDRM package (magic <c>\x7FPKG</c>, byte 0x04 = 0x80, pkg_type at 0x07 = 1).
    /// </summary>
    internal static readonly byte[] Ps3RetailKey =
    [
        0x2E, 0x7B, 0x71, 0xD7, 0xC9, 0xC9, 0xA1, 0x4E,
        0xA3, 0x22, 0x1F, 0x18, 0x88, 0x28, 0xB8, 0xF8,
    ];

    /// <summary>
    /// Retail AES-128 key for every finalized PSP NPDRM package (magic <c>\x7FPKG</c>,
    /// byte 0x04 = 0x80, pkg_type at 0x07 = 2, <c>pkg_key_id &amp; 7 = 1</c>).
    /// </summary>
    internal static readonly byte[] PspRetailKey =
    [
        0x07, 0xF2, 0xC6, 0x82, 0x90, 0xB5, 0x0D, 0x2C,
        0x33, 0x81, 0x8D, 0x70, 0x9B, 0x60, 0xE6, 0x2B,
    ];

    /// <summary>
    /// Base AES-128 key used to derive the per-package session key for PS Vita
    /// application packages (<c>pkg_key_id &amp; 7 = 2</c>). The session key is
    /// <c>AES-128-ECB(base_key, pkg_data_riv)</c>.
    /// </summary>
    internal static readonly byte[] VitaAppBaseKey =
    [
        0xE3, 0x1A, 0x70, 0xC9, 0xCE, 0x1D, 0xD7, 0x2B,
        0xF3, 0xC0, 0x62, 0x29, 0x63, 0xF2, 0xEC, 0xCB,
    ];

    /// <summary>
    /// Base AES-128 key used to derive the per-package session key for PS Vita
    /// LiveArea packages (<c>pkg_key_id &amp; 7 = 3</c>). The session key is
    /// <c>AES-128-ECB(base_key, pkg_data_riv)</c>.
    /// </summary>
    internal static readonly byte[] VitaLiveAreaBaseKey =
    [
        0x42, 0x3A, 0xCA, 0x3A, 0x2B, 0xD5, 0x64, 0x9F,
        0x96, 0x86, 0xAB, 0xAD, 0x6F, 0xD8, 0x80, 0x1F,
    ];

    /// <summary>
    /// Base AES-128 key used to derive the per-package session key for PSM
    /// packages that ride on the PS Vita PKG format (<c>pkg_key_id &amp; 7 = 4</c>).
    /// The session key is <c>AES-128-ECB(base_key, pkg_data_riv)</c>.
    /// </summary>
    internal static readonly byte[] VitaPsmBaseKey =
    [
        0xAF, 0x07, 0xFD, 0x59, 0x65, 0x25, 0x27, 0xBA,
        0xF1, 0x33, 0x89, 0x66, 0x8B, 0x17, 0xD9, 0xEA,
    ];

    // NPDRM header field offsets.
    private const int MagicByte0 = 0x7F;
    private const int FinalizedByteOffset = 0x04;
    private const byte FinalizedRetail = 0x80;
    private const int PkgTypeOffset = 0x07;
    private const byte PkgTypePs3 = 0x01;
    private const byte PkgTypePsp = 0x02;
    private const int MetadataOffsetField = 0x08;   // u32 big-endian
    private const int MetadataCountField = 0x0C;    // u32 big-endian
    private const int ItemCountField = 0x14;        // u32 big-endian
    private const int DataOffsetField = 0x20;       // u64 big-endian
    private const int DataSizeField = 0x28;         // u64 big-endian
    private const int PkgFileKeyOffset = 0x70;      // 16 bytes: initial counter seed
    private const int PkgFileKeyLength = 16;

    // Extended header field offsets. Only present on PSP and PS Vita packages.
    private const int ExtHeaderMagicOffset = 0xC0;   // u32 big-endian, must equal ".ext"
    private const uint ExtHeaderMagic = 0x7F657874;
    private const int PkgKeyIdByteOffset = 0xE7;     // low byte of u32 big-endian at 0xE4

    // Read enough of the file to cover the full main header (0xC0), the extended
    // header magic word (0xC4) and the extended-header pkg_key_id field which ends
    // at 0xE8. A PS3 package (no ext header) has zeros or random bytes past 0xC0
    // and is served correctly by the same read because the ext parsing runs only
    // when the caller has classified the package as PSP or Vita.
    private const int HeaderReadSize = 0xE8;

    // NPDRM metadata packet type identifiers walked to locate PS Vita-specific
    // structures. Each packet is a big-endian pair (u32 type, u32 data_size)
    // followed by data_size bytes of payload.
    private const uint MetaTypeItemsInfo = 13;   // packs items_offset + items_size
    private const uint MetaTypePkgSfo = 14;      // packs sfo_offset + sfo_size

    // Absolute plaintext-region bound. A metadata SFO or item table larger than this
    // is refused rather than allocated. Real Vita PARAM.SFOs stay well under 32 KiB
    // and item tables cover a low thousands of entries at 32 bytes each.
    private const int MaxMetadataPayloadSize = 16 * 1024 * 1024;

    // File-table entry layout. name_offset and name_size are u32 big-endian, data_offset
    // and data_size are u64 big-endian, the two byte fields sit at 0x18 (content_type)
    // and 0x1B (file_type) inside the entry's last four bytes, and the trailing u32 at
    // 0x1C is padding. name_offset and data_offset are expressed relative to the
    // header's data_offset field (same space as the decrypted output of this decryptor).
    private const int FileTableEntrySize = 32;
    private const int EntryFieldNameOffset = 0x00;
    private const int EntryFieldNameSize = 0x04;
    private const int EntryFieldDataOffset64 = 0x08;
    private const int EntryFieldDataSize64 = 0x10;
    private const int EntryFieldContentType = 0x18;
    private const int EntryFieldFileType = 0x1B;

    private const int AesBlockSize = 16;

    // Guardrails so a malformed or hostile header cannot force a gigabyte-sized
    // allocation. Real PS3, PSP and PS Vita packages sit well under these caps: entry
    // counts stay in the low thousands, file names in the low hundreds of bytes, and
    // the individual files this decryptor is used to fetch (PARAM.SFO, ICON0.PNG)
    // sit under a megabyte apiece. The metadata packet-count guard mirrors the same
    // scale: a real NPDRM header emits under two dozen metadata packets.
    private const int MaxFileTableEntries = 65536;
    private const int MaxFileNameLength = 4096;
    private const int MaxMetadataPacketCount = 256;

    // Bound on a single ExtractEntry call. Anything past this cap is refused rather
    // than allocated - the decryptor is used to pull small metadata files, not a
    // package's full payload.
    private const int MaxSingleFileExtractSize = 16 * 1024 * 1024;

    // Bounded chunk size used by DecryptRange so peak memory stays predictable even
    // for a large extract. 64 KiB is 4096 AES blocks, which keeps the intermediate
    // counter and keystream buffers small while amortising per-chunk overhead.
    private const int DecryptChunkSize = 64 * 1024;

    /// <summary>
    /// Walks the encrypted file table of a retail PS3, PSP or PS Vita package and
    /// yields one <see cref="PkgFileEntry"/> per named file it holds. Yields nothing
    /// when the header cannot be parsed, when the finalized byte or <c>pkg_type</c>
    /// do not match <paramref name="type"/>, when the entry count sits outside the
    /// accepted range, or when the encrypted region is truncated relative to the
    /// header's <c>data_offset</c> plus <c>data_size</c>.
    /// </summary>
    /// <param name="source">Random-access source over the whole package file.</param>
    /// <param name="type">Requested family; must be <see cref="PkgType.PS3"/>,
    /// <see cref="PkgType.PSP"/> or <see cref="PkgType.PSVita"/>.</param>
    public static IEnumerable<PkgFileEntry> EnumerateEntries(RandomAccessByteSource source, PkgType type)
    {
        if (source is null)
            yield break;

        if (!TryReadHeader(source, type, out byte[] pkgFileKey, out byte[] aesKey,
            out long dataOffset, out long dataSize))
            yield break;

        long itemsOffset;
        long entryCount;

        if (type == PkgType.PSVita)
        {
            // A Vita package stores the item table at an offset carried in the
            // metadata (packet type 13). The item count is the main-header field
            // at 0x14 rather than the derived-from-first-entry trick used for the
            // older families: Vita packages routinely emit a metadata block between
            // the item array and the first name string, so the first name_offset
            // is not entry_count * 32.
            if (!TryReadVitaLayout(source, out _, out _, out int headerItemCount,
                out long vitaItemsOffset, out _, out _))
                yield break;
            itemsOffset = vitaItemsOffset;
            entryCount = headerItemCount;
            if (entryCount <= 0 || entryCount > MaxFileTableEntries)
                yield break;
            if (itemsOffset < 0 || itemsOffset > dataSize)
                yield break;
            long entriesByteLength = entryCount * FileTableEntrySize;
            if (entriesByteLength > int.MaxValue || itemsOffset + entriesByteLength > dataSize)
                yield break;
        }
        else
        {
            // Decrypt the first entry to read its name_offset, which equals the byte
            // length of the entry array (entry_count * 32) because the earliest name
            // string is emitted immediately after the last entry.
            byte[]? firstEntry = DecryptRange(source, pkgFileKey, aesKey, dataOffset, 0, FileTableEntrySize);
            if (firstEntry is null || firstEntry.Length < FileTableEntrySize)
                yield break;

            uint firstNameOffset = BinaryPrimitives.ReadUInt32BigEndian(
                firstEntry.AsSpan(EntryFieldNameOffset, 4));
            if (firstNameOffset < FileTableEntrySize || firstNameOffset % FileTableEntrySize != 0)
                yield break;
            entryCount = firstNameOffset / FileTableEntrySize;
            if (entryCount <= 0 || entryCount > MaxFileTableEntries)
                yield break;
            long entriesByteLength = entryCount * FileTableEntrySize;
            if (entriesByteLength > int.MaxValue || entriesByteLength > dataSize)
                yield break;
            itemsOffset = 0;
        }

        long tableByteLength = entryCount * FileTableEntrySize;
        // Decrypt the entire entry table in one pass. Even a large table (a few
        // thousand entries) is under 128 KiB.
        byte[]? table = DecryptRange(source, pkgFileKey, aesKey, dataOffset,
            itemsOffset, (int)tableByteLength);
        if (table is null || table.Length < tableByteLength)
            yield break;

        for (long i = 0; i < entryCount; i++)
        {
            int entryOffset = (int)(i * FileTableEntrySize);
            uint nameOffset = BinaryPrimitives.ReadUInt32BigEndian(
                table.AsSpan(entryOffset + EntryFieldNameOffset, 4));
            uint nameSize = BinaryPrimitives.ReadUInt32BigEndian(
                table.AsSpan(entryOffset + EntryFieldNameSize, 4));
            // Data offset and size are u64 big-endian on all three families. Reading
            // the low four bytes (as older tooling does) matches only while files stay
            // under 4 GiB; the full u64 keeps large-content PS Vita packages correct.
            ulong entryDataOffset64 = BinaryPrimitives.ReadUInt64BigEndian(
                table.AsSpan(entryOffset + EntryFieldDataOffset64, 8));
            ulong entryDataSize64 = BinaryPrimitives.ReadUInt64BigEndian(
                table.AsSpan(entryOffset + EntryFieldDataSize64, 8));
            byte contentType = table[entryOffset + EntryFieldContentType];
            byte fileType = table[entryOffset + EntryFieldFileType];

            if (nameSize == 0 || nameSize > MaxFileNameLength)
                continue;
            // Every name lives inside the encrypted region. For PS3 and PSP that
            // means past the entry array (names sit immediately after the last
            // entry), which the itemsOffset==0 branch enforces. Vita packages emit
            // a metadata block between the entry array and the first name string,
            // so the name may sit past the entry array by an arbitrary distance;
            // only the "within data_size" bound is portable across families.
            if (itemsOffset == 0 && nameOffset < tableByteLength)
                continue;
            long nameEnd = (long)nameOffset + nameSize;
            if (nameEnd > dataSize)
                continue;

            byte[]? nameBytes = DecryptRange(source, pkgFileKey, aesKey, dataOffset,
                nameOffset, (int)nameSize);
            if (nameBytes is null)
                continue;

            // Filenames are ASCII and NUL-terminated within the declared name_size.
            int nulLen = 0;
            while (nulLen < nameBytes.Length && nameBytes[nulLen] != 0)
                nulLen++;
            if (nulLen == 0)
                continue;

            string name = Encoding.ASCII.GetString(nameBytes, 0, nulLen);

            // Refuse an entry whose 64-bit offset or size cannot be handled by the
            // int64-indexed decryption path. Real packages stay far below this bound.
            if (entryDataOffset64 > (ulong)long.MaxValue || entryDataSize64 > (ulong)long.MaxValue)
                continue;
            long entryDataOffset = (long)entryDataOffset64;
            long entryDataSize = (long)entryDataSize64;

            uint flags = ((uint)contentType << 24) | fileType;
            yield return new PkgFileEntry(name, entryDataOffset, entryDataSize, flags);
        }
    }

    /// <summary>
    /// Decrypts and returns the bytes of one file named by <paramref name="entry"/>.
    /// Returns null when the header does not parse, when the entry falls outside the
    /// encrypted region, or when the payload is larger than
    /// <see cref="MaxSingleFileExtractSize"/>.
    /// </summary>
    public static byte[]? ExtractEntry(RandomAccessByteSource source, PkgFileEntry entry, PkgType type)
    {
        if (source is null)
            return null;
        if (entry.DataSize <= 0 || entry.DataSize > MaxSingleFileExtractSize)
            return null;

        if (!TryReadHeader(source, type, out byte[] pkgFileKey, out byte[] aesKey,
            out long dataOffset, out long dataSize))
            return null;

        if (entry.DataOffset < 0 || entry.DataOffset + entry.DataSize > dataSize)
            return null;

        return DecryptRange(source, pkgFileKey, aesKey, dataOffset, entry.DataOffset,
            (int)entry.DataSize);
    }

    /// <summary>
    /// Reads the PARAM.SFO carried directly in the header of a retail PS Vita package
    /// (packet type 14 of the NPDRM metadata block). The Vita installer keeps this
    /// copy plaintext outside the encrypted region so a caller can render title
    /// metadata without owning the per-package session key. Returns null when the
    /// package is not a Vita retail package, when the header cannot be parsed, when
    /// the metadata table does not carry a type 14 packet, or when the referenced
    /// range falls outside the file.
    /// </summary>
    public static byte[]? ReadVitaPlaintextSfo(RandomAccessByteSource source)
    {
        if (source is null)
            return null;

        if (!TryReadHeader(source, PkgType.PSVita, out _, out _, out _, out _))
            return null;

        if (!TryReadVitaLayout(source, out _, out _, out _, out _,
            out long sfoOffset, out int sfoSize))
            return null;

        if (sfoOffset <= 0 || sfoSize <= 0 || sfoSize > MaxMetadataPayloadSize)
            return null;
        if (sfoOffset > source.Size - sfoSize)
            return null;

        byte[] sfo = new byte[sfoSize];
        int total = 0;
        while (total < sfoSize)
        {
            int n = source.ReadAt(sfoOffset + total, sfo.AsSpan(total));
            if (n <= 0)
                return null;
            total += n;
        }
        return sfo;
    }

    // Reads the NPDRM header, matches pkg_type against the requested family, and
    // picks the AES-128 CTR key. For PS3 and PSP the key is a constant retail key
    // shared by every package. For PS Vita the key is a per-package session key
    // formed by AES-128-ECB encrypting the counter seed at 0x70 with a base key
    // selected by the extended-header field pkg_key_id (low three bits of the u32
    // at 0xE4). Also returns the per-package counter seed and the byte window of
    // the encrypted region so a caller can bound its reads. Returns false on any
    // shape mismatch (bad magic, non-retail finalized byte, pkg_type outside the
    // supported values, missing or unrecognised ext header on a Vita package, or a
    // data_offset/data_size range that falls outside the file).
    private static bool TryReadHeader(
        RandomAccessByteSource source,
        PkgType expected,
        out byte[] pkgFileKey,
        out byte[] aesKey,
        out long dataOffset,
        out long dataSize)
    {
        pkgFileKey = [];
        aesKey = [];
        dataOffset = 0;
        dataSize = 0;

        if (source.Size < HeaderReadSize)
            return false;

        byte[] header = new byte[HeaderReadSize];
        int total = 0;
        while (total < HeaderReadSize)
        {
            int n = source.ReadAt(total, header.AsSpan(total));
            if (n <= 0)
                return false;
            total += n;
        }

        if (header[0] != MagicByte0
            || header[1] != (byte)'P'
            || header[2] != (byte)'K'
            || header[3] != (byte)'G')
            return false;
        if (header[FinalizedByteOffset] != FinalizedRetail)
            return false;

        byte pkgType = header[PkgTypeOffset];
        switch (expected)
        {
            case PkgType.PS3 when pkgType == PkgTypePs3:
                aesKey = Ps3RetailKey;
                break;
            case PkgType.PSP when pkgType == PkgTypePsp:
                aesKey = PspRetailKey;
                break;
            case PkgType.PSVita when pkgType == PkgTypePsp:
                if (!TryDeriveVitaSessionKey(header, out aesKey))
                    return false;
                break;
            default:
                return false;
        }
        if (aesKey.Length == 0)
            return false;

        ulong rawDataOffset = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(DataOffsetField, 8));
        ulong rawDataSize = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(DataSizeField, 8));
        if (rawDataOffset == 0 || rawDataSize == 0)
            return false;
        // A package larger than 8 EiB is not representable in the on-disk layout.
        if (rawDataOffset > long.MaxValue || rawDataSize > long.MaxValue)
            return false;
        dataOffset = (long)rawDataOffset;
        dataSize = (long)rawDataSize;
        long fileSize = source.Size;
        if (dataOffset < 0 || dataSize < 0 || dataOffset > fileSize - dataSize)
            return false;

        pkgFileKey = new byte[PkgFileKeyLength];
        Buffer.BlockCopy(header, PkgFileKeyOffset, pkgFileKey, 0, PkgFileKeyLength);
        return true;
    }

    // Derives the per-package Vita session key by AES-128-ECB encrypting the counter
    // seed (bytes 0x70-0x80 of the main header) with the base key selected by the
    // low three bits of the extended-header field pkg_key_id (u32 big-endian at
    // 0xE4, so byte 0xE7 carries the low byte). The four accepted key-id types are
    // 1 (PSP), 2 (PS Vita application), 3 (PS Vita LiveArea) and 4 (PSM). A PSP
    // package that lands on this branch is served by the same retail PSP key that
    // TryReadHeader picks for PkgType.PSP so callers that mis-classify a PSP as
    // Vita still receive a plaintext they can validate. Returns false when the ext
    // header is missing, when the key-id byte is outside the accepted set, or when
    // the ext header's magic does not match ".ext".
    private static bool TryDeriveVitaSessionKey(byte[] header, out byte[] sessionKey)
    {
        sessionKey = [];

        // Every Vita package carries the ext header; refuse when it is absent so a
        // caller cannot receive a mis-derived key.
        uint extMagic = BinaryPrimitives.ReadUInt32BigEndian(
            header.AsSpan(ExtHeaderMagicOffset, 4));
        if (extMagic != ExtHeaderMagic)
            return false;

        int keyType = header[PkgKeyIdByteOffset] & 7;
        byte[] baseKey = keyType switch
        {
            1 => PspRetailKey,
            2 => VitaAppBaseKey,
            3 => VitaLiveAreaBaseKey,
            4 => VitaPsmBaseKey,
            _ => [],
        };
        if (baseKey.Length == 0)
            return false;

        // PSP key-id uses the base key directly (matches the CTR-key convention for
        // PSP packages served through the Vita installer). Vita/LiveArea/PSM run one
        // AES-128-ECB block over the counter seed with the base key to produce the
        // per-package session key that becomes the CTR key.
        byte[] iv = new byte[PkgFileKeyLength];
        Buffer.BlockCopy(header, PkgFileKeyOffset, iv, 0, PkgFileKeyLength);

        if (keyType == 1)
        {
            sessionKey = baseKey;
            return true;
        }

        byte[] derived = new byte[PkgFileKeyLength];
        Buffer.BlockCopy(iv, 0, derived, 0, PkgFileKeyLength);
        Aes128 baseCipher = new Aes128(baseKey);
        baseCipher.EncryptBlock(derived);
        sessionKey = derived;
        return true;
    }

    // Walks the NPDRM metadata table to recover the two PS Vita-specific fields the
    // encrypted item walk and the plaintext SFO reader need: items_offset (packet
    // type 13) and sfo_offset + sfo_size (packet type 14). Also returns the main
    // header's own metadata offset, metadata count and item_count so a caller does
    // not need to re-read the header. Returns false when the header itself does not
    // parse or when the metadata table is truncated relative to the file size.
    private static bool TryReadVitaLayout(
        RandomAccessByteSource source,
        out long metaOffset,
        out int metaCount,
        out int itemCount,
        out long itemsOffset,
        out long sfoOffset,
        out int sfoSize)
    {
        metaOffset = 0;
        metaCount = 0;
        itemCount = 0;
        itemsOffset = 0;
        sfoOffset = 0;
        sfoSize = 0;

        if (source.Size < HeaderReadSize)
            return false;

        byte[] header = new byte[HeaderReadSize];
        int total = 0;
        while (total < HeaderReadSize)
        {
            int n = source.ReadAt(total, header.AsSpan(total));
            if (n <= 0)
                return false;
            total += n;
        }

        uint rawMetaOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(MetadataOffsetField, 4));
        uint rawMetaCount = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(MetadataCountField, 4));
        uint rawItemCount = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(ItemCountField, 4));
        if (rawMetaOffset == 0 || rawMetaCount == 0)
            return false;
        if (rawMetaCount > MaxMetadataPacketCount)
            return false;
        if (rawItemCount == 0 || rawItemCount > MaxFileTableEntries)
            return false;

        long fileSize = source.Size;
        long walkOffset = rawMetaOffset;
        byte[] header8 = new byte[8];
        for (uint i = 0; i < rawMetaCount; i++)
        {
            if (walkOffset < 0 || walkOffset > fileSize - 8)
                return false;

            int n = source.ReadAt(walkOffset, header8);
            if (n < 8)
                return false;

            uint packetType = BinaryPrimitives.ReadUInt32BigEndian(header8.AsSpan(0, 4));
            uint payloadSize = BinaryPrimitives.ReadUInt32BigEndian(header8.AsSpan(4, 4));
            if (payloadSize > MaxMetadataPayloadSize)
                return false;
            if (walkOffset + 8 + payloadSize > fileSize)
                return false;

            if ((packetType == MetaTypeItemsInfo || packetType == MetaTypePkgSfo)
                && payloadSize >= 8)
            {
                byte[] payload = new byte[8];
                int pRead = source.ReadAt(walkOffset + 8, payload);
                if (pRead < 8)
                    return false;

                uint first = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4));
                uint second = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4, 4));

                if (packetType == MetaTypeItemsInfo)
                {
                    itemsOffset = first;
                }
                else
                {
                    sfoOffset = first;
                    if (second > MaxMetadataPayloadSize)
                        return false;
                    sfoSize = (int)second;
                }
            }

            walkOffset += 8L + payloadSize;
        }

        metaOffset = rawMetaOffset;
        metaCount = (int)rawMetaCount;
        itemCount = (int)rawItemCount;
        return true;
    }

    // AES-CTR keystream generation + XOR against the ciphertext read from disk.
    // A relativeOffset that is not on a 16-byte boundary reads the enclosing
    // aligned window so the counter arithmetic stays exact, then returns the
    // requested slice. Processing runs in DecryptChunkSize-sized chunks so peak
    // memory does not scale with the requested length.
    private static byte[]? DecryptRange(
        RandomAccessByteSource source,
        byte[] pkgFileKey,
        byte[] aesKey,
        long dataOffset,
        long relativeOffset,
        int length)
    {
        if (length <= 0)
            return [];
        if (relativeOffset < 0)
            return null;

        long alignedStart = relativeOffset & ~((long)AesBlockSize - 1);
        int prefix = (int)(relativeOffset - alignedStart);
        long totalNeededLong = (long)prefix + length;
        long alignedLengthLong = (totalNeededLong + AesBlockSize - 1) & ~((long)AesBlockSize - 1);
        if (alignedLengthLong > int.MaxValue)
            return null;
        int alignedLength = (int)alignedLengthLong;

        byte[] output = new byte[alignedLength];

        byte[] counter = new byte[AesBlockSize];
        Buffer.BlockCopy(pkgFileKey, 0, counter, 0, AesBlockSize);
        long initialBlockIndex = alignedStart / AesBlockSize;
        AddToCounter(counter, initialBlockIndex);

        Aes128 aes = new Aes128(aesKey);
        Span<byte> keystreamBlock = stackalloc byte[AesBlockSize];

        int processed = 0;
        while (processed < alignedLength)
        {
            int thisChunk = Math.Min(DecryptChunkSize, alignedLength - processed);

            // Read the ciphertext for this chunk directly into the output buffer,
            // where it will be XORed against the keystream in place below.
            long absoluteOffset = dataOffset + alignedStart + processed;
            int read = 0;
            while (read < thisChunk)
            {
                int n = source.ReadAt(absoluteOffset + read,
                    output.AsSpan(processed + read, thisChunk - read));
                if (n <= 0)
                    return null;
                read += n;
            }

            // AES-128 CTR keystream is produced one block at a time by encrypting the
            // running counter with the retail key. The plaintext block is the
            // ciphertext block XORed against the keystream block.
            int blocksInChunk = thisChunk / AesBlockSize;
            for (int i = 0; i < blocksInChunk; i++)
            {
                counter.AsSpan(0, AesBlockSize).CopyTo(keystreamBlock);
                aes.EncryptBlock(keystreamBlock);
                int outputBase = processed + i * AesBlockSize;
                for (int b = 0; b < AesBlockSize; b++)
                    output[outputBase + b] ^= keystreamBlock[b];
                IncrementCounter(counter);
            }

            processed += thisChunk;
        }

        if (prefix == 0 && alignedLength == length)
            return output;

        byte[] trimmed = new byte[length];
        Buffer.BlockCopy(output, prefix, trimmed, 0, length);
        return trimmed;
    }

    // Adds an unsigned value in [0, long.MaxValue] to a 16-byte big-endian counter in
    // place. Carries propagate from the low byte upward, matching AES-CTR's convention
    // that treats the counter as a single 128-bit big-endian integer.
    private static void AddToCounter(byte[] counter, long delta)
    {
        if (delta <= 0)
            return;
        ulong d = (ulong)delta;
        ulong carry = 0;
        int i = counter.Length - 1;
        while (i >= 0 && (d != 0 || carry != 0))
        {
            ulong sum = counter[i] + (d & 0xFF) + carry;
            counter[i] = (byte)sum;
            carry = sum >> 8;
            d >>= 8;
            i--;
        }
    }

    // Increments a 16-byte big-endian counter by one in place. Carries propagate from
    // the low byte upward; a wrap-around from the top back to zero is the AES-CTR
    // convention on a 128-bit counter and is left to happen naturally.
    private static void IncrementCounter(byte[] counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            counter[i]++;
            if (counter[i] != 0)
                return;
        }
    }
}
