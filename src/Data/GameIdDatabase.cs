// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Emulator;
using ProsperoMultiTools.Shell;
using SharpProspero.Interop;
using SharpProspero.Storage;
using System;
using System.Collections.Generic;
using System.Text;

namespace ProsperoMultiTools.Data;

/// <summary>
/// Reads the two disc-serial to title lists the classic emulators carry (ps1ids.txt, ps2ids.txt) and
/// answers a title for a serial without a PARAM.SFO to read from. The lists are semicolon-separated
/// pairs, one per line: <c>SERIAL;Title</c>. Each list is loaded on first look-up and cached for the
/// life of the process; a re-read is available for the case a scan runs during a hot install.
/// </summary>
internal static class GameIdDatabase
{
    private static readonly object Lock = new();
    private static Dictionary<string, string>? _ps1;
    private static Dictionary<string, string>? _ps2;

    /// <summary>
    /// Returns the title for a PS1 disc serial (case-insensitive, with or without a hyphen), or an
    /// empty string when the serial is not in the list or the list is not reachable.
    /// </summary>
    public static string LookupPs1(string gameId)
    {
        Dictionary<string, string>? map = Load(ref _ps1, ConfigDatabase.GetPs1IdsDatabasePath());
        return LookupNormalized(map, gameId);
    }

    /// <summary>
    /// Returns the PS1 disc serial that names <paramref name="title"/> (hyphenated <c>SLES-01234</c>
    /// form the cover library keys by), or an empty string when nothing matches. The look-up is
    /// case-insensitive and ignores common suffixes the file names carry that the list does not
    /// (region tags <c>(USA)</c>, <c>(Europe)</c>, disc numbers <c>(Disc 1)</c>, and the like).
    /// This lets a filename that carries only a title still land a cover from the shared library.
    /// </summary>
    public static string LookupPs1SerialByTitle(string title)
    {
        if (string.IsNullOrEmpty(title))
            return string.Empty;
        Dictionary<string, string>? map = Load(ref _ps1, ConfigDatabase.GetPs1IdsDatabasePath());
        if (map is null || map.Count == 0)
            return string.Empty;

        string key = NormalizeTitle(title);
        if (key.Length == 0)
            return string.Empty;
        Dictionary<string, string>? reverse = System.Threading.Volatile.Read(ref _ps1TitleToSerial);
        if (reverse is null)
        {
            lock (Lock)
            {
                reverse = _ps1TitleToSerial;
                if (reverse is null)
                {
                    reverse = new Dictionary<string, string>(map.Count, StringComparer.Ordinal);
                    foreach (KeyValuePair<string, string> kv in map)
                    {
                        string norm = NormalizeTitle(kv.Value);
                        if (norm.Length == 0 || reverse.ContainsKey(norm))
                            continue;
                        // Serial in the .txt is already the hyphenated cover-library form. Store it.
                        reverse[norm] = kv.Key;
                    }
                    _ps1TitleToSerial = reverse;
                }
            }
        }

        if (reverse.TryGetValue(key, out string? exact))
            return exact;

        // A file name commonly carries the title plus a region or disc tag ("Metal Gear Solid (USA)
        // (Disc 1)"). Progressively drop parenthesised tails until the remainder matches an entry.
        string trimmed = title;
        while (true)
        {
            int paren = trimmed.LastIndexOf('(');
            if (paren <= 0)
                break;
            trimmed = trimmed.Substring(0, paren).Trim();
            string k = NormalizeTitle(trimmed);
            if (k.Length == 0)
                break;
            if (reverse.TryGetValue(k, out string? matched))
                return matched;
        }

        return string.Empty;
    }

    private static Dictionary<string, string>? _ps1TitleToSerial;

    // A title look-up must ignore case, punctuation and whitespace so a file named
    // "Crash Bandicoot!" matches the list's "Crash Bandicoot". Every non-alphanumeric character is
    // dropped and every letter is upper-cased.
    private static string NormalizeTitle(string title)
    {
        var sb = new StringBuilder(title.Length);
        foreach (char c in title)
        {
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
                sb.Append(c);
            else if (c is >= 'a' and <= 'z')
                sb.Append((char)(c - ('a' - 'A')));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns the title for a PS2 disc serial (case-insensitive, with or without a hyphen), or an
    /// empty string when the serial is not in the list or the list is not reachable.
    /// </summary>
    public static string LookupPs2(string gameId)
    {
        Dictionary<string, string>? map = Load(ref _ps2, ConfigDatabase.GetPs2IdsDatabasePath());
        return LookupNormalized(map, gameId);
    }

    /// <summary>Drops the cached copies so a next look-up rereads the on-disk lists.</summary>
    public static void Reset()
    {
        lock (Lock)
        {
            _ps1 = null;
            _ps2 = null;
            _ps1TitleToSerial = null;
        }
    }

    private static Dictionary<string, string>? Load(ref Dictionary<string, string>? slot, string path)
    {
        Dictionary<string, string>? snapshot = System.Threading.Volatile.Read(ref slot);
        if (snapshot is not null)
            return snapshot;

        lock (Lock)
        {
            if (slot is not null)
                return slot;

            Dictionary<string, string>? map = TryRead(path);
            slot = map;
            return map;
        }
    }

    private static Dictionary<string, string>? TryRead(string path)
    {
        byte[]? bytes = ReadBytes(path);
        if (bytes is null || bytes.Length == 0)
            return null;

        string text = Encoding.UTF8.GetString(bytes);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == '\n')
            {
                int end = i;
                if (end > start && text[end - 1] == '\r')
                    end--;
                ParseLine(text, start, end, map);
                start = i + 1;
            }
        }
        return map;
    }

    // Reads a file into a byte array via the broker for /data and /user paths and via the direct
    // file-system call for everywhere else. Returns null when neither route can answer, so a USB
    // or /app0 candidate (which is not on a broker partition) transparently falls through to the
    // direct branch while a /data candidate is served by the broker.
    private static byte[]? ReadBytes(string path)
    {
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
        {
            var (outcome, bytes) = SandboxBroker.ReadAllBytes(path);
            if (outcome == BrokerOutcome.Ok && bytes.Length > 0)
                return bytes;
        }

        try
        {
            if (FileSystem.Exists(path))
                return FileSystem.ReadAllBytes(path);
        }
        catch (ProsperoException) { }

        return null;
    }

    private static void ParseLine(string text, int start, int end, Dictionary<string, string> map)
    {
        if (end - start <= 0)
            return;

        int sep = -1;
        for (int i = start; i < end; i++)
        {
            if (text[i] == ';')
            {
                sep = i;
                break;
            }
        }
        if (sep < 0)
            return;

        string key = text[start..sep].Trim();
        string value = text[(sep + 1)..end].Trim();
        if (key.Length == 0 || value.Length == 0)
            return;

        string normalized = Normalize(key);
        if (normalized.Length > 0 && !map.ContainsKey(normalized))
            map[normalized] = value;
    }

    // The lookup is keyed by the disc serial with every non-alphanumeric character stripped and every
    // letter upper-cased, so a serial with or without a hyphen ("SLES-01234", "SLES01234") answers to
    // the same key. The two lists ship in slightly different shapes: the PS1 list keeps the hyphen,
    // the PS2 list drops it. This normalises both.
    private static string Normalize(string id)
    {
        if (string.IsNullOrEmpty(id))
            return string.Empty;
        var sb = new StringBuilder(id.Length);
        foreach (char c in id)
        {
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
                sb.Append(c);
            else if (c is >= 'a' and <= 'z')
                sb.Append((char)(c - ('a' - 'A')));
        }
        return sb.ToString();
    }

    private static string LookupNormalized(Dictionary<string, string>? map, string gameId)
    {
        if (map is null || map.Count == 0)
            return string.Empty;
        string key = Normalize(gameId);
        if (key.Length == 0)
            return string.Empty;
        return map.TryGetValue(key, out string? value) ? value : string.Empty;
    }
}
