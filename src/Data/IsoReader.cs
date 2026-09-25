// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using System;
using System.Text;

namespace ProsperoMultiTools.Data;

internal sealed class IsoVolumeDescriptor
{
    public string SystemId { get; set; } = "";
    public string VolumeId { get; set; } = "";
    public long VolumeSize { get; set; }
    public string Publisher { get; set; } = "";
    public string Application { get; set; } = "";
    public string CreationDate { get; set; } = "";
}

internal static class IsoReader
{
    private const int SectorSize = 2048;
    private const int PrimaryVolumeDescriptorSector = 16;

    /// <summary>
    /// Reads the primary volume descriptor from the ISO image at <paramref name="path"/>.
    /// The caller-facing path variant opens the right byte-source route (direct or broker)
    /// through <see cref="RandomAccessByteSource.Open"/> and delegates to the source variant.
    /// </summary>
    public static IsoVolumeDescriptor? ReadVolumeDescriptor(string path)
    {
        using RandomAccessByteSource? source = RandomAccessByteSource.Open(path);
        if (source is null)
            return null;
        return ReadVolumeDescriptor(source);
    }

    /// <summary>
    /// Reads the primary volume descriptor from an ISO image whose bytes are served by
    /// <paramref name="source"/>. Random-access read at sector 16 is enough; nothing beyond
    /// that sector is touched.
    /// </summary>
    public static IsoVolumeDescriptor? ReadVolumeDescriptor(RandomAccessByteSource source)
    {
        long requiredSize = (long)(PrimaryVolumeDescriptorSector + 1) * SectorSize;
        if (source.Size < requiredSize)
            return null;

        byte[] sector = new byte[SectorSize];
        int read = ReadFully(source, (long)PrimaryVolumeDescriptorSector * SectorSize, sector);
        if (read < SectorSize)
            return null;

        return ReadVolumeDescriptorFromSector(sector, 0);
    }

    public static IsoVolumeDescriptor? ReadVolumeDescriptorFromBytes(byte[] data)
    {
        int offset = PrimaryVolumeDescriptorSector * SectorSize;
        if (offset + SectorSize > data.Length)
            return null;
        return ReadVolumeDescriptorFromSector(data, offset);
    }

    private static IsoVolumeDescriptor? ReadVolumeDescriptorFromSector(byte[] data, int offset)
    {
        if (offset + SectorSize > data.Length)
            return null;

        if (data[offset] != 1)
            return null;
        if (data[offset + 1] != (byte)'C' || data[offset + 2] != (byte)'D' ||
            data[offset + 3] != (byte)'0' || data[offset + 4] != (byte)'0' || data[offset + 5] != (byte)'1')
            return null;

        var desc = new IsoVolumeDescriptor
        {
            SystemId = ReadAsciiField(data, offset + 8, 32),
            VolumeId = ReadAsciiField(data, offset + 40, 32),
            VolumeSize = ReadBothEndianUInt32(data, offset + 80) * SectorSize,
            Publisher = ReadAsciiField(data, offset + 318, 128),
            Application = ReadAsciiField(data, offset + 574, 128),
            CreationDate = ReadDateField(data, offset + 813),
        };

        return desc;
    }

    /// <summary>
    /// Reads a file from within an ISO 9660 image by walking the directory tree.
    /// Returns the file contents, or <c>null</c> when the ISO is invalid or the path is not found.
    /// The path-taking overload opens a byte source through
    /// <see cref="RandomAccessByteSource.Open"/> so an image on a broker-only partition still
    /// yields its embedded files.
    /// </summary>
    /// <param name="isoPath">Filesystem path to the ISO file.</param>
    /// <param name="relativePath">Slash-separated path within the ISO (e.g. "PSP_GAME/PARAM.SFO").</param>
    public static byte[]? ReadFile(string isoPath, string relativePath)
    {
        using RandomAccessByteSource? source = RandomAccessByteSource.Open(isoPath);
        if (source is null)
            return null;
        return ReadFile(source, relativePath);
    }

    /// <summary>
    /// Reads a file from within an ISO 9660 image whose bytes are served by
    /// <paramref name="source"/>. Walks the directory tree via <see cref="RandomAccessByteSource.ReadAt"/>;
    /// a single opened source is reused for the PVD, every directory record, and the target file.
    /// </summary>
    public static byte[]? ReadFile(RandomAccessByteSource source, string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return null;

        long fileSize = source.Size;
        long requiredSize = (long)(PrimaryVolumeDescriptorSector + 1) * SectorSize;
        if (fileSize < requiredSize)
            return null;

        byte[] pvd = new byte[SectorSize];
        int read = ReadFully(source, (long)PrimaryVolumeDescriptorSector * SectorSize, pvd);
        if (read < SectorSize)
            return null;

        // Validate the primary volume descriptor signature.
        if (pvd[0] != 1 ||
            pvd[1] != (byte)'C' || pvd[2] != (byte)'D' ||
            pvd[3] != (byte)'0' || pvd[4] != (byte)'0' || pvd[5] != (byte)'1')
            return null;

        // The root directory record starts at byte 156 in the PVD.
        int dirLba = ReadLittleEndianInt32(pvd, 156 + 2);
        int dirSize = ReadLittleEndianInt32(pvd, 156 + 10);

        string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        for (int seg = 0; seg < segments.Length; seg++)
        {
            if (dirSize <= 0 || dirSize > 16 * 1024 * 1024)
                return null;

            byte[] dirData = new byte[dirSize];
            read = ReadFully(source, (long)dirLba * SectorSize, dirData);
            if (read < dirSize)
                return null;

            bool found = false;
            int offset = 0;
            while (offset < dirData.Length)
            {
                int recordLen = dirData[offset];
                if (recordLen == 0)
                {
                    // A zero-length record means the rest of this sector is padding; skip to the next.
                    int nextSector = ((offset / SectorSize) + 1) * SectorSize;
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
                    string name = Encoding.ASCII.GetString(dirData, offset + 33, nameLen);
                    // Strip the ";1" version suffix that ISO 9660 appends to file identifiers.
                    int semicolon = name.IndexOf(';');
                    if (semicolon >= 0)
                        name = name.Substring(0, semicolon);
                    // Strip any trailing dot left on directory names.
                    if (name.Length > 0 && name[name.Length - 1] == '.')
                        name = name.Substring(0, name.Length - 1);

                    if (string.Equals(name, segments[seg], StringComparison.OrdinalIgnoreCase))
                    {
                        if (seg == segments.Length - 1 && !isDir)
                        {
                            // This is the target file. Read and return its contents.
                            if (entrySize <= 0 || entrySize > 16 * 1024 * 1024)
                                return null;
                            byte[] fileData = new byte[entrySize];
                            read = ReadFully(source, (long)entryLba * SectorSize, fileData);
                            return read >= entrySize ? fileData : null;
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

    public static bool IsIsoFile(byte[] data)
    {
        if (data.Length < (PrimaryVolumeDescriptorSector + 1) * SectorSize)
            return false;
        int offset = PrimaryVolumeDescriptorSector * SectorSize;
        return data[offset] == 1 &&
               data[offset + 1] == (byte)'C' && data[offset + 2] == (byte)'D' &&
               data[offset + 3] == (byte)'0' && data[offset + 4] == (byte)'0' && data[offset + 5] == (byte)'1';
    }

    // Reads exactly <paramref name="buffer"/>.Length bytes when the source has that many left,
    // paging as needed. A source that returns short reads (the broker-backed variant does when a
    // range is larger than one round-trip payload) has its results stitched together here so
    // callers can assume a single ReadAt covers the whole buffer.
    private static int ReadFully(RandomAccessByteSource source, long offset, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = source.ReadAt(offset + total, buffer.Slice(total));
            if (n <= 0)
                break;
            total += n;
        }
        return total;
    }

    private static string ReadAsciiField(byte[] data, int offset, int length)
    {
        if (offset + length > data.Length)
            length = data.Length - offset;
        if (length <= 0)
            return "";
        int end = offset + length;
        while (end > offset && (data[end - 1] == 0x20 || data[end - 1] == 0))
            end--;
        if (end <= offset)
            return "";
        return Encoding.ASCII.GetString(data, offset, end - offset);
    }

    private static long ReadBothEndianUInt32(byte[] data, int offset)
    {
        if (offset + 4 > data.Length)
            return 0;
        return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
    }

    private static int ReadLittleEndianInt32(byte[] data, int offset)
    {
        if (offset + 4 > data.Length)
            return 0;
        return data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);
    }

    private static string ReadDateField(byte[] data, int offset)
    {
        if (offset + 16 > data.Length)
            return "";
        string raw = Encoding.ASCII.GetString(data, offset, 16);
        if (raw.Length >= 14 && raw[0] != '0')
        {
            return $"{raw.Substring(0, 4)}-{raw.Substring(4, 2)}-{raw.Substring(6, 2)} {raw.Substring(8, 2)}:{raw.Substring(10, 2)}:{raw.Substring(12, 2)}";
        }
        return "";
    }
}
