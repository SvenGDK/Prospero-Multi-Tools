// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Emulator;
using ProsperoMultiTools.Shell;
using SharpProspero.Storage;
using System;
using System.IO;

namespace ProsperoMultiTools.Data.SceSysBuilder;

/// <summary>
/// Copies the shell-visible image assets (<c>icon0.png</c>, <c>pic0.png</c>, <c>pic1.png</c>,
/// <c>save_data.png</c>) from the shared platform-asset folder into a staging folder's
/// <c>sce_sys</c> subdirectory. Only files the staging folder does not already carry are
/// written; a folder that already ships its own icon keeps that icon.
/// </summary>
/// <remarks>
/// Assets live under <c>&lt;emulator-resources&gt;/&lt;platform&gt;_assets/</c>. The base folder
/// is resolved through the same search order the rest of the emulator resources use, so a user
/// override on <c>/data</c> takes precedence over the USB copy and the app-bundled fallback.
/// The path resolution routes through the sandbox broker when the resource base lives on a
/// partition the module's mount namespace does not bind (<c>/data</c>, <c>/user</c>).
/// </remarks>
internal static class AssetInjector
{
    /// <summary>The four shell-visible image files a title is expected to ship.</summary>
    private static readonly string[] AssetFiles =
    [
        "icon0.png",
        "pic0.png",
        "pic1.png",
        "save_data.png",
    ];

    /// <summary>
    /// Copies missing image assets into <paramref name="stagingSceSysDir"/> for the given
    /// platform. Returns the number of files written. Files that already exist in the staging
    /// folder are kept; files that do not exist in the platform asset folder are silently
    /// skipped, so a folder that only carries <c>icon0.png</c> populates that one without
    /// failing when <c>save_data.png</c> is not available.
    /// </summary>
    internal static int EnsureAssets(string stagingSceSysDir, GamePlatform platform)
    {
        if (string.IsNullOrEmpty(stagingSceSysDir))
            throw new ArgumentException("Staging sce_sys folder is required.", nameof(stagingSceSysDir));

        string? platformFolder = platform switch
        {
            GamePlatform.PS1 => "ps1_assets",
            GamePlatform.PS2 => "ps2_assets",
            GamePlatform.PSP => "psp_assets",
            _ => null,
        };
        if (platformFolder is null)
            return 0;

        int written = 0;
        foreach (string name in AssetFiles)
        {
            string target = stagingSceSysDir.TrimEnd('/') + "/" + name;
            if (FileExists(target))
                continue;
            string? src = FindAsset(platformFolder, name);
            if (src is null)
                continue;
            byte[]? data = ReadAllBytes(src);
            if (data is null || data.Length == 0)
                continue;
            if (WriteAllBytes(target, data))
                written++;
        }
        return written;
    }

    // Resolves a single asset (icon0.png, ...) by walking the emulator-resources search order and
    // returning the first base whose ../<platformFolder>/<name> file exists.
    private static string? FindAsset(string platformFolder, string name)
    {
        foreach (string root in ResourceBases)
        {
            string path = root + "/" + platformFolder + "/" + name;
            if (FileExists(path))
                return path;
        }
        return null;
    }

    // Same search order as ConfigDatabase's, so a user's own resource pack under /data takes
    // precedence over a USB copy and over the app-bundled fallback under /app0.
    private static readonly string[] ResourceBases =
    [
        "/data/homebrew/emulator-resources",
        "/mnt/usb0/homebrew/emulator-resources",
        "/mnt/usb1/homebrew/emulator-resources",
        "/mnt/usb2/homebrew/emulator-resources",
        "/mnt/usb3/homebrew/emulator-resources",
        "/mnt/usb4/homebrew/emulator-resources",
        "/mnt/usb5/homebrew/emulator-resources",
        "/mnt/usb6/homebrew/emulator-resources",
        "/mnt/usb7/homebrew/emulator-resources",
        "/app0/homebrew/emulator-resources",
    ];

    private static bool FileExists(string path)
    {
        if (FileSystem.Exists(path))
            return true;
        return SandboxBroker.IsOnBrokerPartition(path)
            && SandboxBroker.IsReachable()
            && SandboxBroker.FileExists(path);
    }

    private static byte[]? ReadAllBytes(string path)
    {
        try
        {
            if (FileSystem.Exists(path))
                return FileSystem.ReadAllBytes(path);
        }
        catch (SharpProspero.Interop.ProsperoException) { }
        catch (IOException) { }

        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
        {
            var (outcome, bytes) = SandboxBroker.ReadAllBytes(path);
            if (outcome == BrokerOutcome.Ok && bytes.Length > 0)
                return bytes;
        }
        return null;
    }

    private static bool WriteAllBytes(string path, byte[] data)
    {
        try
        {
            FileSystem.WriteAllBytes(path, data);
            return true;
        }
        catch (SharpProspero.Interop.ProsperoException) { }
        catch (IOException) { }

        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            return SandboxBroker.WriteAllBytes(path, data) == BrokerOutcome.Ok;
        return false;
    }
}
