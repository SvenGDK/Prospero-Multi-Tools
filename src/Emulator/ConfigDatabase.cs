// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Shell;
using SharpProspero.Storage;
using System;

namespace ProsperoMultiTools.Emulator;

/// <summary>
/// Reads per-game configuration files from the on-device emulator resources directory.
/// Loads extracted TXT, LUA, PS3 config, and widescreen patch files from the
/// <c>ps2-configs/</c> subdirectories.
/// <para>
/// Search order for emulator resources: <c>/data/homebrew/emulator-resources</c> (primary),
/// <c>/mnt/usb0..7/homebrew/emulator-resources</c>, <c>/app0/homebrew/emulator-resources</c>
/// (fallback). First hit per file wins.
/// </para>
/// <para>
/// The <c>.dat</c> archive files (UDF images) are not parsed directly; they require
/// extraction at deployment time. This class operates on the already-extracted files
/// in the <c>txt/</c>, <c>lua/</c>, <c>ps3/</c>, and <c>widescreen/</c> subdirectories.
/// </para>
/// <para>
/// The primary base sits under <c>/data</c> and USB bases sit under <c>/mnt/usbN</c>; those two
/// partitions live outside the module's mount namespace. Every existence check and file read on
/// them is routed through <see cref="SandboxBroker"/> when the daemon is reachable, so a caller
/// does not need to know whether the direct file-system call would answer with EINVAL. The
/// <c>/app0</c> base binds inside the sandbox and is read directly.
/// </para>
/// </summary>
internal static class ConfigDatabase
{
    // Search order: /data first (writeable resource pack), then USB0..7 (user-installed), then
    // /app0 (bundled fallback). First hit per file wins - the walker returns the first base whose
    // path exists, so a user's USB override takes effect only when nothing sits at the same
    // relative path under /data.
    private static readonly string[] ResourceBases = new[]
    {
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
    };

    /// <summary>
    /// Checks whether a TXT config exists for the given PS2 game ID.
    /// </summary>
    /// <param name="gameId">PS2 disc serial with dots/underscores (e.g. "SLES_012.34").</param>
    public static bool HasTxtConfig(string gameId)
        => ResolveFile("/ps2-configs/txt/" + gameId) is not null;

    /// <summary>
    /// Reads the TXT config for the given PS2 game ID, or returns null.
    /// </summary>
    public static string? ReadTxtConfig(string gameId)
    {
        string? path = ResolveFile("/ps2-configs/txt/" + gameId);
        return path is null ? null : ReadTextFile(path);
    }

    /// <summary>
    /// Checks whether a LUA config exists for the given PS2 game ID.
    /// </summary>
    public static bool HasLuaConfig(string gameId)
        => ResolveFile("/ps2-configs/lua/" + gameId) is not null;

    /// <summary>
    /// Reads the LUA config for the given PS2 game ID, or returns null.
    /// </summary>
    public static string? ReadLuaConfig(string gameId)
    {
        string? path = ResolveFile("/ps2-configs/lua/" + gameId);
        return path is null ? null : ReadTextFile(path);
    }

    /// <summary>
    /// Checks whether a PS3 compatibility config exists for the given PS2 game ID.
    /// </summary>
    /// <param name="gameId">PS2 disc serial with dots/underscores (e.g. "SLES_012.34").</param>
    public static bool HasPs3Config(string gameId)
        => ResolveFile("/ps2-configs/ps3/" + gameId + ".CONFIG") is not null;

    /// <summary>
    /// Returns the on-device path to the PS3 compatibility config for the given game ID,
    /// or null if it does not exist under any resource base.
    /// </summary>
    public static string? GetPs3ConfigPath(string gameId)
        => ResolveFile("/ps2-configs/ps3/" + gameId + ".CONFIG");

    /// <summary>
    /// Checks whether a widescreen LUA patch exists for the given CRC.
    /// </summary>
    /// <param name="gameCrc">Game ELF CRC (uppercase hex, e.g. "1A2B3C4D").</param>
    public static bool HasWidescreenPatch(string gameCrc)
        => ResolveFile("/ps2-configs/widescreen/" + gameCrc + ".lua") is not null;

    /// <summary>
    /// Reads the widescreen LUA patch for the given CRC, or returns null.
    /// </summary>
    public static string? ReadWidescreenPatch(string gameCrc)
    {
        string? path = ResolveFile("/ps2-configs/widescreen/" + gameCrc + ".lua");
        return path is null ? null : ReadTextFile(path);
    }

    /// <summary>
    /// Returns the on-device base path for the lua_include shared LUA library directory. Walks
    /// the resource-base search order and returns the first base whose <c>lua_include</c>
    /// directory exists; falls back to the primary <c>/data</c> base when nothing resolves so
    /// the caller always sees a non-null path.
    /// </summary>
    public static string GetLuaIncludePath()
        => ResolveDirectory("/lua_include") ?? (ResourceBases[0] + "/lua_include");

    /// <summary>
    /// Returns the on-device path to the PS2 game ID database file. Falls back to the primary
    /// <c>/data</c> base when nothing resolves so the caller always sees a non-null path.
    /// </summary>
    public static string GetPs2IdsDatabasePath()
        => ResolveFile("/ps2ids.txt") ?? (ResourceBases[0] + "/ps2ids.txt");

    /// <summary>
    /// Returns the on-device path to the PS1 game ID database file. Falls back to the primary
    /// <c>/data</c> base when nothing resolves so the caller always sees a non-null path.
    /// </summary>
    public static string GetPs1IdsDatabasePath()
        => ResolveFile("/ps1ids.txt") ?? (ResourceBases[0] + "/ps1ids.txt");

    // Walks the resource-base search order and returns the first base + relative path whose file
    // exists. The existence check goes through the broker for /data and /user paths (which the
    // module's mount namespace does not bind), and through the direct file-system call for USB
    // and /app0 bases. Returns null when no base carries the file.
    private static string? ResolveFile(string relative)
    {
        foreach (string root in ResourceBases)
        {
            string path = root + relative;
            if (PathExists(path))
                return path;
        }
        return null;
    }

    // Same walk as ResolveFile, but the existence check verifies the path is a directory rather
    // than any reachable node. Returns null when no base carries the directory.
    private static string? ResolveDirectory(string relative)
    {
        foreach (string root in ResourceBases)
        {
            string path = root + relative;
            if (DirectoryExists(path))
                return path;
        }
        return null;
    }

    // Routes an existence check through the broker for /data and /user paths (which the mount
    // namespace does not bind), falling back to the direct call when the broker cannot answer.
    private static bool PathExists(string path)
    {
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            return SandboxBroker.FileExists(path);
        return FileSystem.Exists(path);
    }

    // Routes a directory-existence check through the broker for /data and /user paths (which
    // the mount namespace does not bind). The broker's stat call carries the file-mode bits, so
    // the caller can be sure the reachable node is a directory and not a file that happens to
    // share the name.
    private static bool DirectoryExists(string path)
    {
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            return SandboxBroker.IsDirectory(path);
        return FileSystem.Exists(path);
    }

    // Routes a text read through the broker for /data and /user paths, decoding the UTF-8 bytes
    // the same way the direct-read branch does so callers see one string surface no matter where
    // the bytes came from.
    private static string? ReadTextFile(string path)
    {
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
        {
            var (outcome, data) = SandboxBroker.ReadAllBytes(path);
            if (outcome != BrokerOutcome.Ok || data.Length == 0)
                return null;
            return System.Text.Encoding.UTF8.GetString(data);
        }

        if (!FileSystem.Exists(path))
            return null;

        try
        {
            byte[] data = FileSystem.ReadAllBytes(path);
            if (data.Length == 0)
                return null;
            return System.Text.Encoding.UTF8.GetString(data);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
