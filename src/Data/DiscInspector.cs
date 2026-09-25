// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using SharpProspero.Interop;
using System;
using System.Text;

namespace ProsperoMultiTools.Data;

/// <summary>
/// What sort of disc a data file actually carries. A file name and its extension only claim what
/// the file is; this inspects the bytes to say what it really is, so a PSP UMD image renamed
/// with an .iso extension is not mistaken for a PS2 disc and a PS1 raw-sector image is picked out
/// even when it is a lone .bin.
/// </summary>
internal enum DiscKind
{
    /// <summary>The bytes did not identify a known platform.</summary>
    Unknown,

    /// <summary>PS1 disc: raw 2352-byte sectors carrying a Mode 2 / audio track layout.</summary>
    PS1,

    /// <summary>PS2 disc: 2048-byte sectors with a SYSTEM.CNF pointing at a PS2 boot file.</summary>
    PS2,

    /// <summary>PSP disc: 2048-byte sectors carrying a PSP_GAME directory.</summary>
    PSP,

    /// <summary>PS3 disc: 2048-byte sectors carrying a PS3_GAME directory.</summary>
    PS3,
}

/// <summary>
/// Reads a few small windows out of a disc image and answers what platform it belongs to. Only the
/// bytes needed to decide are read, so a PSP UMD held on a slow USB drive is inspected in a couple
/// of small reads rather than a whole-file scan.
/// </summary>
internal static class DiscInspector
{
    private const long CueRawSectorSize = 2352;

    /// <summary>
    /// Returns what platform the image at <paramref name="path"/> actually belongs to. A file with
    /// no reachable content answers <see cref="DiscKind.Unknown"/>. Opens the right byte-source
    /// route through <see cref="RandomAccessByteSource.Open"/> so a disc image sitting on a
    /// partition the module cannot open directly still gets inspected via the broker.
    /// </summary>
    public static DiscKind Inspect(string path)
    {
        if (string.IsNullOrEmpty(path))
            return DiscKind.Unknown;
        using RandomAccessByteSource? source = RandomAccessByteSource.Open(path);
        if (source is null)
            return DiscKind.Unknown;
        return Inspect(path, source);
    }

    /// <summary>
    /// Returns what platform the image at <paramref name="path"/> actually belongs to, using
    /// <paramref name="source"/> for every byte read. The path is passed alongside the source
    /// because the extension-shape heuristic (for a bare .bin) looks at the file name; every
    /// content probe goes through the source.
    /// </summary>
    public static DiscKind Inspect(string path, RandomAccessByteSource source)
    {
        long size = source.Size;
        if (size <= 0)
            return DiscKind.Unknown;

        string ext = TrimExtensionLower(path);

        // A bare .bin from a PS1 backup is raw 2352-byte sectors and does not carry an ISO 9660
        // volume descriptor; the whole-file byte-per-sector shape settles it without a read.
        if (ext == ".bin")
        {
            if (size % CueRawSectorSize == 0)
                return DiscKind.PS1;
            // Some rippers pad the last sector; a size that is a multiple of 2352 plus a partial
            // sector still shows the raw-sector fingerprint.
            if (size % CueRawSectorSize < 16 || (CueRawSectorSize - size % CueRawSectorSize) < 16)
                return DiscKind.PS1;
        }

        // Every other layout is ISO 9660 sectors (2048 bytes each). Read the primary volume
        // descriptor first: its magic bytes settle whether this is an ISO 9660 image at all.
        IsoVolumeDescriptor? pvd = IsoReader.ReadVolumeDescriptor(source);
        if (pvd is null)
        {
            // Not ISO 9660, but the caller may still have a CUE-referenced .bin here.
            return ext == ".bin" ? DiscKind.PS1 : DiscKind.Unknown;
        }

        // PSP images publish a PSP_GAME directory at the root; that is the strongest signal
        // (both retail UMDs and homebrew ISO rips ship it).
        byte[]? sfoBytes = IsoReader.ReadFile(source, "PSP_GAME/PARAM.SFO");
        if (sfoBytes is not null && sfoBytes.Length > 0)
            return DiscKind.PSP;

        // PS3 images publish a PS3_GAME directory at the root.
        byte[]? ps3Bytes = IsoReader.ReadFile(source, "PS3_GAME/PARAM.SFO");
        if (ps3Bytes is not null && ps3Bytes.Length > 0)
            return DiscKind.PS3;

        // PS2 images publish a SYSTEM.CNF file at the root whose BOOT2 line names the executable.
        // The file is small enough to read whole and then check for the PS2 boot prefix.
        byte[]? sysCnf = IsoReader.ReadFile(source, "SYSTEM.CNF");
        if (sysCnf is not null && sysCnf.Length > 0)
        {
            string text = Encoding.ASCII.GetString(sysCnf);
            // The PS2 SYSTEM.CNF starts with "BOOT2 = cdrom0:\SLES_...;1" and always names BOOT2.
            // A PS1 SYSTEM.CNF names BOOT= (single BOOT, no digit). A PSX disc image is unusual as
            // an ISO because the disc format is raw sectors, but the check is left in for a
            // 2048-byte per-sector PS1 rip that some tools produce.
            if (text.IndexOf("BOOT2", StringComparison.OrdinalIgnoreCase) >= 0)
                return DiscKind.PS2;
            if (text.IndexOf("BOOT ", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("BOOT=", StringComparison.OrdinalIgnoreCase) >= 0)
                return DiscKind.PS1;
        }

        // No genre signal from the well-known files. Fall back to the ISO volume label - but the
        // PS3 label is "PLAYSTATION3" and the PS2 label is "PLAYSTATION" alone, so an image whose
        // label is "PLAYSTATION3" must not read as PS2 (a substring test the other way around
        // would give exactly that). The check for the digit suffix runs first for that reason.
        if (!string.IsNullOrEmpty(pvd.SystemId))
        {
            string sys = pvd.SystemId.ToUpperInvariant();
            if (sys.Contains("PSP"))
                return DiscKind.PSP;
            if (sys.Contains("PLAYSTATION3") || sys.Contains("PLAYSTATION 3"))
                return DiscKind.PS3;
            if (sys.Contains("PLAYSTATION"))
                return DiscKind.PS2;
        }

        return DiscKind.Unknown;
    }

    /// <summary>
    /// True when the image belongs to <paramref name="platform"/>. Returns false for a file whose
    /// bytes say a different platform, which is what keeps a PSP UMD out of the PS2 list even
    /// when both share the .iso extension.
    /// </summary>
    public static bool BelongsTo(string path, GamePlatform platform)
    {
        DiscKind kind = Inspect(path);
        return MatchesPlatform(kind, platform);
    }

    /// <summary>
    /// Same as <see cref="BelongsTo(string, GamePlatform)"/>, but reads bytes through the
    /// caller's byte source so the /data broker route is used when the source was opened there.
    /// </summary>
    public static bool BelongsTo(string path, GamePlatform platform, RandomAccessByteSource source)
    {
        DiscKind kind = Inspect(path, source);
        return MatchesPlatform(kind, platform);
    }

    /// <summary>
    /// Returns false for <see cref="DiscKind.Unknown"/> so unrecognized ISOs do not surface in the
    /// wrong platform browser. Positive per-platform matches require the disc kind to positively
    /// identify as that platform: an ISO whose bytes did not settle a known layout is skipped
    /// entirely rather than tagged with an arbitrary platform.
    /// </summary>
    private static bool MatchesPlatform(DiscKind kind, GamePlatform platform)
    {
        if (kind == DiscKind.Unknown)
            return false;
        return platform switch
        {
            GamePlatform.PS1 => kind == DiscKind.PS1,
            GamePlatform.PS2 => kind == DiscKind.PS2,
            GamePlatform.PS3 => kind == DiscKind.PS3,
            GamePlatform.PSP => kind == DiscKind.PSP,
            _ => false,
        };
    }

    /// <summary>Reads the SFO carried in a PSP disc image at <c>PSP_GAME/PARAM.SFO</c>.</summary>
    public static byte[]? ReadPspParamSfo(string path)
        => IsoReader.ReadFile(path, "PSP_GAME/PARAM.SFO");

    /// <summary>
    /// Reads the SFO carried in a PSP disc image at <c>PSP_GAME/PARAM.SFO</c> using the caller's
    /// byte source so the /data broker route is used when the source was opened there.
    /// </summary>
    public static byte[]? ReadPspParamSfo(RandomAccessByteSource source)
        => IsoReader.ReadFile(source, "PSP_GAME/PARAM.SFO");

    /// <summary>Reads the SFO carried in a PS3 disc image at <c>PS3_GAME/PARAM.SFO</c>.</summary>
    public static byte[]? ReadPs3ParamSfo(string path)
        => IsoReader.ReadFile(path, "PS3_GAME/PARAM.SFO");

    /// <summary>
    /// Reads the SFO carried in a PS3 disc image at <c>PS3_GAME/PARAM.SFO</c> using the caller's
    /// byte source so the /data broker route is used when the source was opened there.
    /// </summary>
    public static byte[]? ReadPs3ParamSfo(RandomAccessByteSource source)
        => IsoReader.ReadFile(source, "PS3_GAME/PARAM.SFO");

    /// <summary>
    /// Reads the disc serial from a PS1 disc image. A PS1 <c>SYSTEM.CNF</c> holds a line of the
    /// form <c>BOOT = cdrom:\SLES_012.26;1</c> naming the boot executable, and the executable
    /// name IS the disc serial once the version separators are dropped. The image uses raw
    /// 2352-byte sectors so an ISO 9660 file table is unreachable; the boot line is found by
    /// scanning the first few MB of the file as ASCII. Returns the disc serial in hyphenated
    /// form (<c>SLES-01226</c>), or an empty string when the line cannot be found.
    /// </summary>
    public static string ReadPs1DiscSerial(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        using RandomAccessByteSource? source = RandomAccessByteSource.Open(path);
        if (source is null)
            return string.Empty;
        return ReadPs1DiscSerial(source);
    }

    /// <summary>
    /// Same as <see cref="ReadPs1DiscSerial(string)"/>, but reads bytes through the caller's byte
    /// source so a raw .bin sitting on a broker-only partition still yields its serial.
    /// </summary>
    public static string ReadPs1DiscSerial(RandomAccessByteSource source)
    {
        // Seven MB covers SYSTEM.CNF placement in every retail PS1 layout while keeping the read
        // cheap on a slow USB drive. If the file is smaller, the whole file is read.
        const int scanBytes = 7 * 1024 * 1024;
        long size = source.Size;
        if (size <= 0)
            return string.Empty;
        int toRead = (int)Math.Min(scanBytes, size);

        byte[] buffer = new byte[toRead];
        int off = 0;
        try
        {
            while (off < toRead)
            {
                int n = source.ReadAt(off, buffer.AsSpan(off, toRead - off));
                if (n <= 0) break;
                off += n;
            }
        }
        catch (ProsperoException)
        {
            return string.Empty;
        }
        if (off == 0)
            return string.Empty;
        if (off < toRead)
            Array.Resize(ref buffer, off);

        // Find "BOOT" followed by an equals sign and the boot line. The line reads
        // "BOOT = cdrom:\SLES_012.26;1" (spacing varies), so the serial sits between the backslash
        // and the semicolon. Only bytes past the equals sign are read as ASCII; the raw sectors that
        // surround SYSTEM.CNF are binary and irrelevant.
        for (int i = 0; i + 4 < buffer.Length; i++)
        {
            if (buffer[i] != (byte)'B' || buffer[i + 1] != (byte)'O'
                || buffer[i + 2] != (byte)'O' || buffer[i + 3] != (byte)'T')
                continue;
            // Reject "BOOT2" (PS2): the following byte must be a space, tab or equals.
            byte next = buffer[i + 4];
            if (next != (byte)' ' && next != (byte)'\t' && next != (byte)'=')
                continue;

            int equals = -1;
            for (int j = i + 4; j < buffer.Length && j < i + 20; j++)
            {
                if (buffer[j] == (byte)'=') { equals = j; break; }
                if (buffer[j] != (byte)' ' && buffer[j] != (byte)'\t') break;
            }
            if (equals < 0) continue;

            int end = -1;
            for (int j = equals + 1; j < buffer.Length && j < equals + 128; j++)
            {
                byte b = buffer[j];
                if (b == 0 || b == (byte)'\r' || b == (byte)'\n') { end = j; break; }
            }
            if (end < 0) continue;

            string line = Encoding.ASCII.GetString(buffer, equals + 1, end - equals - 1).Trim();
            string serial = ExtractPs1SerialFromBootLine(line);
            if (serial.Length > 0)
                return serial;
        }

        return string.Empty;
    }

    // Turns "cdrom:\SLES_012.26;1" into "SLES-01226". The retail layout produces the "cdrom:\"
    // prefix, sometimes a subdirectory such as "MGS\", an underscore between the letters and
    // digits, a dot inside the digits, and a ";1" version suffix. All of that gets stripped so
    // only the four letters and five digits remain, then a hyphen is inserted between them to
    // form the hyphenated shape ("SLES-01226").
    private static string ExtractPs1SerialFromBootLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return string.Empty;

        // Drop the shell prefix. Both "cdrom:\" and "cdrom:" are seen, sometimes preceded by a
        // subdirectory such as "MGS\" the retail Metal Gear Solid disc uses.
        int lastBackslash = line.LastIndexOf('\\');
        int lastColon = line.LastIndexOf(':');
        int start = Math.Max(lastBackslash, lastColon);
        if (start >= 0)
            line = line.Substring(start + 1);

        int semi = line.IndexOf(';');
        if (semi >= 0)
            line = line.Substring(0, semi);

        var sb = new StringBuilder(line.Length);
        foreach (char c in line)
        {
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9'))
                sb.Append(char.ToUpperInvariant(c));
        }
        string flat = sb.ToString();
        // A retail PS1 boot executable is four letters followed by five digits. Anything shorter is
        // treated as unknown so a garbled line does not become a wrong serial.
        if (flat.Length < 9)
            return string.Empty;
        if (!IsLetter(flat[0]) || !IsLetter(flat[1]) || !IsLetter(flat[2]) || !IsLetter(flat[3]))
            return string.Empty;
        for (int i = 4; i < 9; i++)
            if (flat[i] is < '0' or > '9')
                return string.Empty;
        return flat.Substring(0, 4) + "-" + flat.Substring(4, 5);
    }

    /// <summary>Extracts the PS2 disc serial from SYSTEM.CNF (e.g. "SLES-12345").</summary>
    public static string ReadPs2DiscSerial(string path)
    {
        byte[]? sysCnf = IsoReader.ReadFile(path, "SYSTEM.CNF");
        return ExtractPs2DiscSerial(sysCnf);
    }

    /// <summary>
    /// Same as <see cref="ReadPs2DiscSerial(string)"/>, but reads SYSTEM.CNF through the caller's
    /// byte source so a PS2 ISO sitting on a broker-only partition still yields its serial.
    /// </summary>
    public static string ReadPs2DiscSerial(RandomAccessByteSource source)
    {
        byte[]? sysCnf = IsoReader.ReadFile(source, "SYSTEM.CNF");
        return ExtractPs2DiscSerial(sysCnf);
    }

    private static string ExtractPs2DiscSerial(byte[]? sysCnf)
    {
        if (sysCnf is null || sysCnf.Length == 0)
            return string.Empty;

        string text = Encoding.ASCII.GetString(sysCnf);
        // BOOT2 = cdrom0:\SLES_555.55;1
        int boot = text.IndexOf("BOOT2", StringComparison.OrdinalIgnoreCase);
        if (boot < 0)
            return string.Empty;
        int equals = text.IndexOf('=', boot);
        if (equals < 0)
            return string.Empty;

        int backslash = text.IndexOf('\\', equals);
        if (backslash < 0)
            return string.Empty;

        int semicolon = text.IndexOf(';', backslash);
        if (semicolon < 0)
            semicolon = text.Length;

        string boot2 = text.Substring(backslash + 1, semicolon - backslash - 1).Trim();
        // "SLES_555.55" -> "SLES-55555"
        return NormalisePs2Serial(boot2);
    }

    private static string NormalisePs2Serial(string boot2)
    {
        if (string.IsNullOrEmpty(boot2))
            return string.Empty;
        var sb = new StringBuilder(boot2.Length);
        foreach (char c in boot2)
        {
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9'))
                sb.Append(char.ToUpperInvariant(c));
        }
        string flat = sb.ToString();
        if (flat.Length >= 9 && IsLetter(flat[0]) && IsLetter(flat[1]) && IsLetter(flat[2]) && IsLetter(flat[3]))
        {
            return flat.Substring(0, 4) + "-" + flat.Substring(4);
        }
        return flat;
    }

    private static bool IsLetter(char c) => c is >= 'A' and <= 'Z';

    private static string TrimExtensionLower(string path)
    {
        int dot = path.LastIndexOf('.');
        if (dot < 0)
            return string.Empty;
        string ext = path.Substring(dot);
        if (ext.Length > 5)
            return string.Empty;
        var sb = new StringBuilder(ext.Length);
        foreach (char c in ext)
            sb.Append(c >= 'A' && c <= 'Z' ? (char)(c + 32) : c);
        return sb.ToString();
    }
}
