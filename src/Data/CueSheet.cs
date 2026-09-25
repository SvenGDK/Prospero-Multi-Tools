// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using System;
using System.Collections.Generic;
using System.Text;

namespace ProsperoMultiTools.Data;

internal sealed class CueTrack
{
    public int Number { get; set; }
    public string Type { get; set; } = "";
    public string Index00 { get; set; } = "";
    public string Index01 { get; set; } = "";
    public string PreGap { get; set; } = "";
}

internal sealed class CueFile
{
    public string FileName { get; set; } = "";
    public string FileType { get; set; } = "";
    public List<CueTrack> Tracks { get; } = [];
}

internal sealed class CueSheet
{
    public List<CueFile> Files { get; } = [];

    public static CueSheet? Parse(string content)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        if (content.Length > 0 && content[0] == '﻿')
            content = content.Substring(1);

        var sheet = new CueSheet();
        CueFile? currentFile = null;
        CueTrack? currentTrack = null;

        string[] lines = SplitLines(content);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
            {
                currentFile = new CueFile();
                ParseFileDirective(line, currentFile);
                sheet.Files.Add(currentFile);
                currentTrack = null;
            }
            else if (line.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase) && currentFile is not null)
            {
                currentTrack = new CueTrack();
                ParseTrackDirective(line, currentTrack);
                currentFile.Tracks.Add(currentTrack);
            }
            else if (line.StartsWith("INDEX ", StringComparison.OrdinalIgnoreCase) && currentTrack is not null)
            {
                ParseIndexDirective(line, currentTrack);
            }
            else if (line.StartsWith("PREGAP ", StringComparison.OrdinalIgnoreCase) && currentTrack is not null)
            {
                currentTrack.PreGap = line.Substring(7).Trim();
            }
        }

        return sheet.Files.Count > 0 ? sheet : null;
    }

    public static CueSheet? ParseFromFile(string path)
    {
        if (!SharpProspero.Storage.FileSystem.Exists(path))
            return null;
        string content = SharpProspero.Storage.FileSystem.ReadAllText(path);
        return Parse(content);
    }

    public string GetDirectory(string cuePath)
    {
        int lastSlash = cuePath.LastIndexOf('/');
        if (lastSlash >= 0)
            return cuePath.Substring(0, lastSlash);
        return "";
    }

    /// <summary>
    /// Returns candidate on-disk paths for every FILE entry - possibly more than one candidate
    /// per entry when the FILE name is written with a Windows path shape and needs a basename
    /// fallback. Callers that need exactly one path per FILE should use
    /// <see cref="GetBinFilePathsResolved(string, System.Func{string, bool}?)"/>.
    /// </summary>
    public List<string> GetBinFilePaths(string cuePath)
    {
        var paths = new List<string>();
        string dir = GetDirectory(cuePath);
        foreach (CueFile file in Files)
        {
            if (string.IsNullOrEmpty(file.FileName))
                continue;

            (string? subPath, string? basePath) = BuildCandidates(dir, file.FileName);
            if (subPath is null)
                continue;
            paths.Add(subPath);
            if (basePath is not null)
                paths.Add(basePath);
        }
        return paths;
    }

    /// <summary>
    /// Returns exactly one path per FILE entry. When the FILE line names a sub-folder the sub-path
    /// candidate is tried against <paramref name="exists"/> first; on miss the basename in the
    /// cue's own folder is used. When <paramref name="exists"/> is null the sub-path is returned
    /// without a probe so a caller that only needs a count is not forced through the file system.
    /// A FILE entry whose name would climb above the cue's folder (a '..' segment or an
    /// absolute-looking prefix) is rejected: the FILE is skipped and its slot in the returned list
    /// is left out.
    /// </summary>
    public List<string> GetBinFilePathsResolved(string cuePath, Func<string, bool>? exists)
    {
        var resolved = new List<string>();
        string dir = GetDirectory(cuePath);
        foreach (CueFile file in Files)
        {
            if (string.IsNullOrEmpty(file.FileName))
                continue;

            (string? subPath, string? basePath) = BuildCandidates(dir, file.FileName);
            if (subPath is null)
                continue;

            string chosen = subPath;
            if (exists is not null)
            {
                if (exists(subPath))
                    chosen = subPath;
                else if (basePath is not null && exists(basePath))
                    chosen = basePath;
                else
                    chosen = basePath ?? subPath;
            }
            resolved.Add(chosen);
        }
        return resolved;
    }

    // Builds the sub-path and basename candidates for a FILE line. A cue authored on Windows may
    // carry backslashes ("SUB\\GAME.bin") or a full drive path ("C:\\Rips\\GAME.bin"); both are
    // reduced to the basename in the cue's own folder so a copied rip still resolves. A FILE
    // segment that climbs above the cue's own folder ("../foo.bin" or absolute-anchored
    // "/etc/passwd") is refused: only the basename inside the cue's folder is returned, and the
    // subPath is nulled so a caller that iterates the list gets nothing traversal-shaped back.
    private static (string? subPath, string? basePath) BuildCandidates(string dir, string fileName)
    {
        string name = fileName.Replace('\\', '/');
        string baseName = name;
        int lastSlash = baseName.LastIndexOf('/');
        if (lastSlash >= 0)
            baseName = baseName.Substring(lastSlash + 1);

        if (baseName.Length == 0)
            return (null, null);
        if (baseName is "." or "..")
            return (null, null);

        bool traversal = false;
        bool looksAbsolute = name.StartsWith("/", StringComparison.Ordinal)
            || (name.Length >= 2 && name[1] == ':');
        if (looksAbsolute)
            traversal = true;
        else
        {
            foreach (string segment in name.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".." || segment == ".")
                {
                    traversal = true;
                    break;
                }
            }
        }

        string basePath = string.IsNullOrEmpty(dir) ? baseName : $"{dir}/{baseName}";
        if (traversal)
            return (basePath, null);

        string subPath = string.IsNullOrEmpty(dir) ? name : $"{dir}/{name}";
        if (string.Equals(subPath, basePath, StringComparison.Ordinal))
            return (subPath, null);
        return (subPath, basePath);
    }

    private static void ParseFileDirective(string line, CueFile file)
    {
        int firstQuote = line.IndexOf('"');
        if (firstQuote >= 0)
        {
            int secondQuote = line.IndexOf('"', firstQuote + 1);
            if (secondQuote > firstQuote)
            {
                file.FileName = line.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
                string rest = line.Substring(secondQuote + 1).Trim();
                file.FileType = rest;
            }
        }
        else
        {
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                file.FileName = parts[1];
                file.FileType = parts[2];
            }
        }
    }

    private static void ParseTrackDirective(string line, CueTrack track)
    {
        string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3)
        {
            if (int.TryParse(parts[1], out int num))
                track.Number = num;
            track.Type = parts[2];
        }
    }

    private static void ParseIndexDirective(string line, CueTrack track)
    {
        string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3)
        {
            string indexNum = parts[1];
            string timestamp = parts[2];
            if (indexNum == "00")
                track.Index00 = timestamp;
            else if (indexNum == "01")
                track.Index01 = timestamp;
        }
    }

    public static long TimestampToSectors(string timestamp)
    {
        string[] parts = timestamp.Split(':');
        if (parts.Length != 3)
            return 0;
        if (!int.TryParse(parts[0], out int minutes) ||
            !int.TryParse(parts[1], out int seconds) ||
            !int.TryParse(parts[2], out int frames))
            return 0;
        return (long)minutes * 60 * 75 + (long)seconds * 75 + frames;
    }

    private static string[] SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                int end = i;
                if (end > start && text[end - 1] == '\r')
                    end--;
                lines.Add(text.Substring(start, end - start));
                start = i + 1;
            }
        }
        if (start < text.Length)
            lines.Add(text.Substring(start));
        return lines.ToArray();
    }
}
