// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using System;
using System.Text;

namespace ProsperoMultiTools.Data;

/// <summary>
/// Computes the PS2 boot ELF checksum a widescreen-patch database is keyed by. The value is a
/// 32-bit XOR sum of every little-endian 32-bit word in the ELF file, formatted as eight
/// uppercase hex characters with leading zeros preserved (the shape widescreen databases use for
/// their file names, e.g. <c>0001171A</c>). Bytes past the last full word (0-3 trailing bytes)
/// are dropped from the sum, matching the shape a byte-for-byte comparison against a widescreen
/// database file expects.
/// </summary>
internal static class Ps2ElfCrc
{
    /// <summary>
    /// Reads the boot ELF from the PS2 disc image at <paramref name="sourceIsoPath"/> and returns
    /// its eight-character uppercase hex checksum. The disc image is opened once and reused for
    /// both the SYSTEM.CNF read and the ELF read so a slow USB-hosted image is not re-opened.
    /// Returns <c>null</c> when the image is unreachable, when SYSTEM.CNF is missing, when the
    /// BOOT2 line does not name an ELF, or when the ELF file is not present in the image.
    /// </summary>
    public static string? ComputeFromIso(string sourceIsoPath)
    {
        if (string.IsNullOrEmpty(sourceIsoPath))
            return null;

        using RandomAccessByteSource? source = RandomAccessByteSource.Open(sourceIsoPath);
        if (source is null)
            return null;

        byte[]? sysCnf = IsoReader.ReadFile(source, "SYSTEM.CNF");
        if (sysCnf is null || sysCnf.Length == 0)
            return null;

        string bootName = ExtractBoot2Name(sysCnf);
        if (bootName.Length == 0)
            return null;

        byte[]? elf = IsoReader.ReadFile(source, bootName);
        if (elf is null || elf.Length < 4)
            return null;

        return ComputeElfChecksumHex(elf);
    }

    // Parses the BOOT2 line of a PS2 SYSTEM.CNF and returns the ELF file name in the shape it
    // appears in the disc's directory record (e.g. "SLES_012.34"), suitable for a direct
    // IsoReader.ReadFile lookup. Returns the empty string when the line is missing or malformed.
    // A retail SYSTEM.CNF looks like "BOOT2 = cdrom0:\SLES_012.34;1" (whitespace varies); a
    // per-title backup may drop the drive prefix ("BOOT2 = \SLES_012.34;1") but always keeps the
    // backslash before the file name and the ";1" version suffix after it. The parse takes only
    // the substring between the last backslash before ';' and ';' itself so both shapes work.
    private static string ExtractBoot2Name(byte[] sysCnf)
    {
        // SYSTEM.CNF is a short ASCII file (a few hundred bytes at most in practice). Cap the
        // decode at 16 KiB anyway so a corrupted or misidentified file cannot burn a large decode.
        int len = Math.Min(sysCnf.Length, 16 * 1024);
        string text = Encoding.ASCII.GetString(sysCnf, 0, len);

        int boot = text.IndexOf("BOOT2", StringComparison.OrdinalIgnoreCase);
        if (boot < 0)
            return string.Empty;

        int equals = text.IndexOf('=', boot);
        if (equals < 0)
            return string.Empty;

        // A newline before the semicolon means the line has no ";1" suffix (unusual, but seen in
        // some rippers); stop the search at the line boundary so the following line's ';' does
        // not accidentally close the token.
        int lineEnd = text.IndexOfAny(['\r', '\n'], equals);
        if (lineEnd < 0)
            lineEnd = text.Length;

        int semicolon = text.IndexOf(';', equals, lineEnd - equals);
        int end = semicolon >= 0 ? semicolon : lineEnd;

        int backslash = text.LastIndexOf('\\', end - 1, end - equals);
        if (backslash < 0)
        {
            // No drive prefix at all: fall back to the token that follows the '=' sign.
            string bare = text.Substring(equals + 1, end - equals - 1).Trim();
            return bare;
        }

        return text.Substring(backslash + 1, end - backslash - 1).Trim();
    }

    // Sums every complete little-endian 32-bit word in <paramref name="elf"/> with XOR and returns
    // the result as an eight-character uppercase hex string with leading zeros preserved. Trailing
    // bytes past the last full word are ignored (an ELF's file size is 4-byte aligned in every
    // retail layout; a truncated dump loses those bytes from the sum, the same way a widescreen
    // database's entry would have been produced).
    private static string ComputeElfChecksumHex(byte[] elf)
    {
        uint sum = 0;
        int end = elf.Length & ~3;
        for (int i = 0; i < end; i += 4)
        {
            uint word = elf[i]
                | ((uint)elf[i + 1] << 8)
                | ((uint)elf[i + 2] << 16)
                | ((uint)elf[i + 3] << 24);
            sum ^= word;
        }
        return sum.ToString("X8");
    }
}
