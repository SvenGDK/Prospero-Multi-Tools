// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using System;

namespace ProsperoMultiTools.Data;

/// <summary>
/// Reads an ISO 9660 file system laid out inside a raw PS1 disc image (a BIN file whose bytes
/// still carry the CD physical sector framing). A retail PS1 disc is authored in MODE2 Form 1 or
/// MODE1 with 2352-byte physical sectors; each physical sector carries a 12-byte sync pattern,
/// a 4-byte header, 8 bytes of subheader (MODE2 only), 2048 bytes of user data, then an EDC/ECC
/// tail. Standard ISO 9660 assumes the 2048-byte user-data view; this reader detects the physical
/// layout from sector 16 and adapts every read so the ISO 9660 walker sees the same bytes it would
/// see over a plain 2048-byte image.
/// </summary>
internal static class BinIsoReader
{
    private const int PhysicalSectorSize = 2352;
    private const int LogicalSectorSize = 2048;
    private const int Mode1UserDataOffset = 16;   // sync (12) + header (4)
    private const int Mode2UserDataOffset = 24;   // sync (12) + header (4) + subheader (8)
    private const int PrimaryVolumeDescriptorSector = 16;

    private static ReadOnlySpan<byte> SyncPattern =>
    [
        0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00,
    ];

    /// <summary>
    /// Probes the physical layout of the BIN file at sector 16 and returns the offset (in bytes)
    /// from the start of each 2352-byte physical sector to the 2048-byte user-data block.
    /// Returns -1 when the file is not a raw CD image (no sync pattern, or the file is smaller
    /// than sector 16 + one physical sector).
    /// </summary>
    public static int DetectUserDataOffset(RandomAccessByteSource source)
    {
        long required = (long)(PrimaryVolumeDescriptorSector + 1) * PhysicalSectorSize;
        if (source.Size < required)
            return -1;

        // A single 24-byte read at the start of physical sector 16 shows whether the sync pattern
        // is there and whether the PVD signature lands at offset 16 (MODE1) or 24 (MODE2). The
        // sync pattern alone identifies a raw CD image; the offset choice is picked by peeking at
        // the following bytes for the ISO 9660 volume descriptor signature ("\x01CD001").
        Span<byte> head = stackalloc byte[Mode2UserDataOffset + 6];
        int read = source.ReadAt((long)PrimaryVolumeDescriptorSector * PhysicalSectorSize, head);
        if (read < head.Length)
            return -1;

        if (!head[..12].SequenceEqual(SyncPattern))
            return -1;

        if (LooksLikePvd(head, Mode1UserDataOffset))
            return Mode1UserDataOffset;
        if (LooksLikePvd(head, Mode2UserDataOffset))
            return Mode2UserDataOffset;
        return -1;
    }

    /// <summary>
    /// Walks the ISO 9660 directory tree carried inside a raw CD image and returns the bytes of
    /// the file at <paramref name="relativePath"/>. The path is slash-separated
    /// ("SYSTEM.CNF"). Returns <c>null</c> when the image is not a raw CD image, when the file is
    /// not present, or when the read is truncated. Only touches the sectors that carry the PVD,
    /// the walked directory records, and the target file.
    /// </summary>
    public static byte[]? ReadFile(RandomAccessByteSource source, string relativePath, int maxBytes = 1024 * 1024)
    {
        if (string.IsNullOrEmpty(relativePath))
            return null;

        int userOffset = DetectUserDataOffset(source);
        if (userOffset < 0)
            return null;

        byte[]? pvd = ReadLogicalSector(source, PrimaryVolumeDescriptorSector, userOffset);
        if (pvd is null)
            return null;

        // Root directory record at byte 156 of the PVD; LBA at +2 (little-endian uint32), size at +10.
        int dirLba = ReadLittleEndianInt32(pvd, 156 + 2);
        int dirSize = ReadLittleEndianInt32(pvd, 156 + 10);

        string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        for (int seg = 0; seg < segments.Length; seg++)
        {
            if (dirSize <= 0 || dirSize > 16 * 1024 * 1024)
                return null;

            byte[]? dirData = ReadLogicalRange(source, dirLba, dirSize, userOffset);
            if (dirData is null)
                return null;

            bool found = false;
            int offset = 0;
            while (offset < dirData.Length)
            {
                int recordLen = dirData[offset];
                if (recordLen == 0)
                {
                    int nextSector = ((offset / LogicalSectorSize) + 1) * LogicalSectorSize;
                    if (nextSector >= dirData.Length)
                        break;
                    offset = nextSector;
                    continue;
                }
                if (offset + recordLen > dirData.Length || recordLen < 34)
                    break;

                int entryLba = ReadLittleEndianInt32(dirData, offset + 2);
                int entrySize = ReadLittleEndianInt32(dirData, offset + 10);
                byte flags = dirData[offset + 25];
                int nameLen = dirData[offset + 32];
                bool isDir = (flags & 0x02) != 0;

                if (nameLen > 0 && offset + 33 + nameLen <= dirData.Length)
                {
                    string name = System.Text.Encoding.ASCII.GetString(dirData, offset + 33, nameLen);
                    int semi = name.IndexOf(';');
                    if (semi >= 0)
                        name = name.Substring(0, semi);
                    if (name.Length > 0 && name[^1] == '.')
                        name = name.Substring(0, name.Length - 1);

                    if (string.Equals(name, segments[seg], StringComparison.OrdinalIgnoreCase))
                    {
                        if (seg == segments.Length - 1 && !isDir)
                        {
                            if (entrySize <= 0 || entrySize > maxBytes)
                                return null;
                            return ReadLogicalRange(source, entryLba, entrySize, userOffset);
                        }
                        if (seg < segments.Length - 1 && isDir)
                        {
                            dirLba = entryLba;
                            dirSize = entrySize;
                            found = true;
                            break;
                        }
                        return null;
                    }
                }
                offset += recordLen;
            }

            if (!found && seg < segments.Length - 1)
                return null;
        }
        return null;
    }

    /// <summary>
    /// Extracts a PS1 disc serial from the string carried in a SYSTEM.CNF file's BOOT line. The
    /// entry looks like <c>BOOT = cdrom:\SLES_012.34;1</c>; the returned value is the four-letter
    /// prefix, a hyphen, and the five digits (<c>SLES-01234</c>). Returns the empty string when
    /// the bytes carry no BOOT line or the extracted token does not fit the disc-serial shape.
    /// </summary>
    public static string ExtractSerialFromSystemCnf(byte[] systemCnf)
    {
        if (systemCnf is null || systemCnf.Length == 0)
            return string.Empty;

        // SYSTEM.CNF is a short text file (a few hundred bytes at most in practice). Cap the ASCII
        // decode at 16 KiB anyway so a corrupted or misidentified file cannot burn a large decode.
        int len = Math.Min(systemCnf.Length, 16 * 1024);
        string text = System.Text.Encoding.ASCII.GetString(systemCnf, 0, len);

        // Search for "BOOT" case-insensitively; the equals sign that follows may have whitespace
        // on either side. Ignore any other line the file carries (TCB=, EVENT=, STACK=, ...).
        int i = 0;
        while (i < text.Length)
        {
            int lineEnd = text.IndexOfAny(['\r', '\n'], i);
            if (lineEnd < 0) lineEnd = text.Length;
            string line = text.Substring(i, lineEnd - i);
            i = lineEnd + 1;

            string trimmed = line.TrimStart();
            if (trimmed.Length < 5)
                continue;
            if (!(trimmed[0] == 'B' || trimmed[0] == 'b')) continue;
            if (!(trimmed[1] == 'O' || trimmed[1] == 'o')) continue;
            if (!(trimmed[2] == 'O' || trimmed[2] == 'o')) continue;
            if (!(trimmed[3] == 'T' || trimmed[3] == 't')) continue;

            int eq = trimmed.IndexOf('=');
            if (eq < 4)
                continue;
            string value = trimmed.Substring(eq + 1).Trim();

            // Strip the "cdrom:\" or "cdrom:" prefix and the ";1" version suffix.
            int colon = value.IndexOf(':');
            if (colon >= 0)
                value = value.Substring(colon + 1);
            value = value.TrimStart('\\', '/');
            int semi = value.IndexOf(';');
            if (semi >= 0)
                value = value.Substring(0, semi);

            // Some dumps route the file through a sub-folder ("MGS\SLUS_005.94;1"). Take only the
            // last path segment.
            int lastSlash = value.LastIndexOfAny(['\\', '/']);
            if (lastSlash >= 0)
                value = value.Substring(lastSlash + 1);

            string normalised = value.Replace("_", "-").Replace(".", "").Trim().ToUpperInvariant();
            if (LooksLikeDiscSerial(normalised))
                return normalised;
        }

        return string.Empty;
    }

    private static bool LooksLikePvd(ReadOnlySpan<byte> head, int offset)
    {
        if (head.Length < offset + 6)
            return false;
        return head[offset] == 1
            && head[offset + 1] == (byte)'C'
            && head[offset + 2] == (byte)'D'
            && head[offset + 3] == (byte)'0'
            && head[offset + 4] == (byte)'0'
            && head[offset + 5] == (byte)'1';
    }

    private static byte[]? ReadLogicalSector(RandomAccessByteSource source, int lba, int userOffset)
    {
        long physical = (long)lba * PhysicalSectorSize + userOffset;
        byte[] buffer = new byte[LogicalSectorSize];
        int read = ReadFully(source, physical, buffer);
        return read == LogicalSectorSize ? buffer : null;
    }

    // Reads <paramref name="length"/> bytes of user data starting at logical sector
    // <paramref name="startLba"/>, stitching together one physical sector at a time so the ECC
    // gap between two user-data blocks is skipped byte-for-byte the way the emulator would.
    private static byte[]? ReadLogicalRange(RandomAccessByteSource source, int startLba, int length, int userOffset)
    {
        if (length <= 0)
            return null;

        int sectorsNeeded = (length + LogicalSectorSize - 1) / LogicalSectorSize;
        byte[] output = new byte[sectorsNeeded * LogicalSectorSize];
        for (int i = 0; i < sectorsNeeded; i++)
        {
            long physical = (long)(startLba + i) * PhysicalSectorSize + userOffset;
            int read = ReadFully(source, physical, output.AsSpan(i * LogicalSectorSize, LogicalSectorSize));
            if (read < LogicalSectorSize)
                return null;
        }
        if (output.Length == length)
            return output;
        byte[] trimmed = new byte[length];
        Buffer.BlockCopy(output, 0, trimmed, 0, length);
        return trimmed;
    }

    private static int ReadFully(RandomAccessByteSource source, long offset, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = source.ReadAt(offset + total, buffer[total..]);
            if (n <= 0)
                break;
            total += n;
        }
        return total;
    }

    private static int ReadLittleEndianInt32(byte[] data, int offset)
    {
        if (offset + 4 > data.Length)
            return 0;
        return data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);
    }

    // AAAA-NNNNN with A being an upper-case letter and N a digit.
    private static bool LooksLikeDiscSerial(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length != 10 || s[4] != '-')
            return false;
        for (int i = 0; i < 4; i++)
        {
            char c = s[i];
            if (c < 'A' || c > 'Z')
                return false;
        }
        for (int i = 5; i < 10; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9')
                return false;
        }
        return true;
    }
}
