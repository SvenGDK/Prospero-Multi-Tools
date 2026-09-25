// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using System;
using System.Text;

namespace ProsperoMultiTools.Shell;

internal static class Formatting
{
    public static string Timestamp(DateTime dt)
    {
        var sb = new StringBuilder(19);
        sb.Append(dt.Year);
        sb.Append('-');
        Pad2(sb, dt.Month);
        sb.Append('-');
        Pad2(sb, dt.Day);
        sb.Append(' ');
        Pad2(sb, dt.Hour);
        sb.Append(':');
        Pad2(sb, dt.Minute);
        sb.Append(':');
        Pad2(sb, dt.Second);
        return sb.ToString();
    }

    public static string Date(DateTime dt)
    {
        var sb = new StringBuilder(10);
        sb.Append(dt.Year);
        sb.Append('-');
        Pad2(sb, dt.Month);
        sb.Append('-');
        Pad2(sb, dt.Day);
        return sb.ToString();
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024L)
            return $"{bytes} B";
        if (bytes < 1024L * 1024)
            return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    public static string FormatRegion(string code)
    {
        if (string.IsNullOrEmpty(code))
            return "Unknown";
        return code;
    }

    private static void Pad2(StringBuilder sb, int value)
    {
        if (value < 10)
            sb.Append('0');
        sb.Append(value);
    }
}
