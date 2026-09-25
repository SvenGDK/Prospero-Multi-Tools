// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using System;
using System.Text;

namespace ProsperoMultiTools.Data.SceSysBuilder;

/// <summary>
/// Rewrites the content-id field inside an existing <c>sce_sys/playgo-chunk.dat</c> file so a
/// title whose main SFO has been re-identified stays consistent with the playgo metadata the
/// shell reads at install and launch. The content id sits at offset <c>0x40</c> and is a
/// 36-byte ASCII string right-padded with zeros; every other field in the file (magic, chunk
/// tables, scenario tables, sizes) is left unchanged.
/// </summary>
internal static class PlayGoChunkDatWriter
{
    /// <summary>Byte offset of the content-id field inside playgo-chunk.dat.</summary>
    internal const int ContentIdOffset = 0x40;

    /// <summary>Length of the content-id field.</summary>
    internal const int ContentIdLength = 36;

    /// <summary>
    /// Returns a copy of <paramref name="existing"/> with the content-id field replaced by
    /// <paramref name="newContentId"/>. Throws when the source buffer is too short to contain
    /// the field, or when the new content id does not fit.
    /// </summary>
    internal static byte[] PatchContentId(byte[] existing, string newContentId)
    {
        if (existing is null || existing.Length < ContentIdOffset + ContentIdLength)
            throw new ArgumentException("Source playgo-chunk.dat is too short.", nameof(existing));
        if (string.IsNullOrEmpty(newContentId))
            throw new ArgumentException("Content id is required.", nameof(newContentId));
        if (newContentId.Length > ContentIdLength)
            throw new ArgumentException("Content id exceeds " + ContentIdLength + " bytes.", nameof(newContentId));

        byte[] result = (byte[])existing.Clone();
        for (int i = 0; i < ContentIdLength; i++)
            result[ContentIdOffset + i] = 0;
        Encoding.ASCII.GetBytes(newContentId).CopyTo(result, ContentIdOffset);
        return result;
    }
}

/// <summary>
/// Provides the canonical <c>sce_sys/playgo-manifest.xml</c> the shell reads when a title
/// ships a single chunk, single scenario, no multi-language packaging. This is the exact XML
/// every fake-pkg toolchain writes for a barebones title and is identical byte-for-byte across
/// every fake-pkg the shell has accepted so far.
/// </summary>
internal static class PlayGoManifestWriter
{
    /// <summary>
    /// The canonical single-chunk single-scenario manifest bytes, including the UTF-8 BOM and
    /// CRLF line endings the shell's XML reader accepts.
    /// </summary>
    internal static byte[] CreateDefault()
    {
        // The exact 368-byte layout the fake-pkg toolchain writes for a single-chunk title.
        const string body =
            "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\r\n" +
            "<psproject fmt=\"playgo-manifest\" version=\"0990\">\r\n" +
            "  <volume>\r\n" +
            "    <chunk_info chunk_count=\"1\" scenario_count=\"1\">\r\n" +
            "      <scenarios default_id=\"0\">\r\n" +
            "        <scenario id=\"0\" type=\"sp\" initial_chunk_count=\"1\" label=\"Scenario #0\">0</scenario>\r\n" +
            "      </scenarios>\r\n" +
            "    </chunk_info>\r\n" +
            "  </volume>\r\n" +
            "</psproject>\r\n";
        byte[] bom  = new byte[] { 0xEF, 0xBB, 0xBF };
        byte[] text = Encoding.UTF8.GetBytes(body);
        byte[] result = new byte[bom.Length + text.Length];
        Buffer.BlockCopy(bom, 0, result, 0, bom.Length);
        Buffer.BlockCopy(text, 0, result, bom.Length, text.Length);
        return result;
    }
}

/// <summary>
/// Produces the fixed 8192-byte <c>sce_sys/psreserved.dat</c> file every title carries. The file
/// is a pure zero-filled buffer; the shell allocates it as a reservation slot for future runtime
/// bookkeeping and does not read any bytes from it during install.
/// </summary>
internal static class PsReservedWriter
{
    /// <summary>Byte length of a psreserved.dat file.</summary>
    internal const int Size = 0x2000;

    /// <summary>Returns an 8192-byte zero-filled buffer.</summary>
    internal static byte[] Create() => new byte[Size];
}
