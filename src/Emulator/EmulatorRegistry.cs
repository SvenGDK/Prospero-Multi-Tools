// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Data;
using ProsperoMultiTools.Shell;
using SharpProspero.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace ProsperoMultiTools.Emulator;

/// <summary>
/// Static registry of known emulator descriptors, loaded from the embedded manifest resource
/// and augmented with an on-device scan of the standard emulator roots so an emulator the user
/// dropped into <c>/data/homebrew/emulators/</c> or <c>/mnt/usbN/homebrew/</c> is available for
/// launch without a manifest edit or an app rebuild.
/// </summary>
internal static class EmulatorRegistry
{
    private static readonly List<EmulatorDescriptor> Descriptors = [];
    private static readonly object DiscoveryLock = new();
    private static bool _discoveryRan;

    static EmulatorRegistry()
    {
        LoadFromEmbeddedManifest();
    }

    /// <summary>All registered emulator descriptors, including auto-discovered ones.</summary>
    public static IReadOnlyList<EmulatorDescriptor> All
    {
        get { EnsureDiscovery(); return Descriptors; }
    }

    /// <summary>
    /// Walks the standard emulator roots (<c>/data/homebrew/emulators/</c> and
    /// <c>/mnt/usbN/homebrew/</c>) and adds every folder that looks like an emulator to the
    /// registry, classifying it by the files it ships. Runs once per process; the caller can
    /// force a re-scan by calling <see cref="Rescan"/>. Manifest entries win on a name conflict
    /// so a user who edits the manifest to override a discovered folder's title id keeps that
    /// override.
    /// </summary>
    public static void EnsureDiscovery()
    {
        if (_discoveryRan) return;
        lock (DiscoveryLock)
        {
            if (_discoveryRan) return;
            RunDiscovery();
            _discoveryRan = true;
        }
    }

    /// <summary>Forces a fresh on-device scan for emulator folders.</summary>
    public static void Rescan()
    {
        lock (DiscoveryLock)
        {
            _discoveryRan = false;
            // Trim the discovered rows (kind is Discovered-source: appended after LoadFromEmbeddedManifest).
            int manifestCount = Descriptors.Count;
            for (int i = Descriptors.Count - 1; i >= 0; i--)
            {
                if (Descriptors[i].AutoDiscovered)
                {
                    Descriptors.RemoveAt(i);
                    manifestCount--;
                }
            }
            RunDiscovery();
            _discoveryRan = true;
        }
    }

    // Scans /data/homebrew/emulators/ + /mnt/usbN/homebrew/ for emulator-shaped folders and
    // adds any that are not already covered by a manifest entry. A folder counts as an emulator
    // when it carries an eboot.bin + sce_sys/param.sfo pair; the kind is picked from the file
    // signatures (ps2-emu-compiler.self => PS2, config-title.txt + data/USER_L0.IMG-ready name
    // => PSP, everything else with config-title.txt => PS1).
    private static void RunDiscovery()
    {
        var roots = new List<string> { "/data/homebrew/emulators" };
        for (int u = 0; u < 8; u++)
            roots.Add("/mnt/usb" + u + "/homebrew");

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Descriptors)
            existing.Add(d.Name);

        foreach (string root in roots)
        {
            List<(string Name, bool IsDirectory)> children = ListDirectoryAny(root);
            if (children.Count == 0)
                continue;

            foreach ((string name, bool isDir) in children)
            {
                if (!isDir) continue;
                if (existing.Contains(name)) continue;

                string folderPath = root + "/" + name;
                if (!LooksLikeEmulator(folderPath, out EmulatorKind kind))
                    continue;

                string sfoPath = folderPath + "/sce_sys/param.sfo";
                (string titleId, string contentId, string title) = ReadEmulatorIdentity(sfoPath);
                if (string.IsNullOrEmpty(titleId))
                    continue;

                // Build the probe list: this discovery pass found the folder at `folderPath`, so
                // that path is the first probe; the standard fallback shape lets a later launch
                // resolve the same emulator from a different root (a user may move the folder
                // between /data and USB between launches).
                var probes = new List<string> { folderPath };
                probes.Add("/data/homebrew/emulators/" + name);
                for (int u = 0; u < 8; u++)
                {
                    string usbProbe = "/mnt/usb" + u + "/homebrew/" + name;
                    if (!probes.Contains(usbProbe))
                        probes.Add(usbProbe);
                }
                string appProbe = "/app0/emulators/" + name;
                if (!probes.Contains(appProbe))
                    probes.Add(appProbe);

                Descriptors.Add(new EmulatorDescriptor(kind, name, titleId, contentId, probes) { AutoDiscovered = true });
                existing.Add(name);
            }
        }
    }

    // Lists a directory, routing through the broker for /data / /user paths and the direct call
    // otherwise. Returns an empty list when neither route answers so a caller can iterate roots
    // without a guard on every one.
    private static List<(string Name, bool IsDirectory)> ListDirectoryAny(string folder)
    {
        var result = new List<(string, bool)>();
        try
        {
            if (FileSystem.TryEnumerateDirectory(folder, out IReadOnlyList<DirectoryEntry> entries, out _))
            {
                foreach (DirectoryEntry entry in entries)
                    if (entry.IsDirectory || entry.IsFile)
                        result.Add((entry.Name, entry.IsDirectory));
                return result;
            }
        }
        catch { }

        if (SandboxBroker.IsOnBrokerPartition(folder) && SandboxBroker.IsReachable())
        {
            var (outcome, brokerEntries) = SandboxBroker.List(folder);
            if (outcome == BrokerOutcome.Ok)
            {
                foreach (BrokerDirEntry entry in brokerEntries)
                    if (entry.IsDirectory || entry.IsFile)
                        result.Add((entry.Name, entry.IsDirectory));
            }
        }
        return result;
    }

    private static bool FileExistsAny(string path)
    {
        try
        {
            if (FileSystem.Exists(path))
                return true;
        }
        catch { }
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
            return SandboxBroker.FileExists(path);
        return false;
    }

    // Every emulator ships eboot.bin + sce_sys/param.sfo. The kind falls out of the extra files
    // each family ships: PS2 has ps2-emu-compiler.self, PS1 has config-title.txt with a
    // data/disc1.bin reference in the base name (or the ps1hd folder name), PSP has
    // config-title.txt paired with a data/USER_L0.IMG or a psphd folder name. A folder that
    // does not carry an eboot.bin is not an emulator and is skipped.
    private static bool LooksLikeEmulator(string folderPath, out EmulatorKind kind)
    {
        kind = EmulatorKind.PS2;
        if (!FileExistsAny(folderPath + "/eboot.bin"))
            return false;
        if (!FileExistsAny(folderPath + "/sce_sys/param.sfo"))
            return false;

        // PS2 signature: the JIT compiler self file is the surest tell.
        if (FileExistsAny(folderPath + "/ps2-emu-compiler.self"))
        {
            kind = EmulatorKind.PS2;
            return true;
        }

        // PS1 / PSP: both use config-title.txt. Fall back to the folder-name convention when the
        // config content is not readable ("ps1hd" / "psphd" are the shipped folder names, but a
        // user-renamed folder still lets the classifier fall through to PS1 as a sane default
        // since PS1 emulators outnumber PSP ones in practice).
        if (FileExistsAny(folderPath + "/config-title.txt"))
        {
            string leaf = folderPath;
            int slash = leaf.LastIndexOf('/');
            if (slash >= 0) leaf = leaf.Substring(slash + 1);
            if (leaf.IndexOf("psp", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                kind = EmulatorKind.PSP;
                return true;
            }
            kind = EmulatorKind.PS1;
            return true;
        }

        // A folder with an eboot but neither compiler.self nor config-title.txt is not one of
        // the three emulator families the launch pipeline knows how to drive.
        return false;
    }

    private static (string TitleId, string ContentId, string Title) ReadEmulatorIdentity(string sfoPath)
    {
        try
        {
            byte[]? bytes = null;
            if (SandboxBroker.IsOnBrokerPartition(sfoPath) && SandboxBroker.IsReachable())
            {
                var (outcome, data) = SandboxBroker.ReadAllBytes(sfoPath);
                if (outcome == BrokerOutcome.Ok && data.Length > 0)
                    bytes = data;
            }
            if (bytes is null)
            {
                if (!FileSystem.Exists(sfoPath))
                    return (string.Empty, string.Empty, string.Empty);
                bytes = FileSystem.ReadAllBytes(sfoPath);
            }
            if (bytes is null || bytes.Length == 0)
                return (string.Empty, string.Empty, string.Empty);
            SharpProspero.Storage.Sfo.SfoFile? sfo = SharpProspero.Storage.Sfo.SfoFile.Read(bytes);
            if (sfo is null)
                return (string.Empty, string.Empty, string.Empty);
            string tid = sfo.GetString("TITLE_ID") ?? string.Empty;
            string cid = sfo.GetString("CONTENT_ID") ?? string.Empty;
            string title = sfo.GetString("TITLE") ?? string.Empty;
            return (tid, cid, title);
        }
        catch
        {
            return (string.Empty, string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// Returns all descriptors matching the given emulator kind.
    /// </summary>
    public static List<EmulatorDescriptor> GetByKind(EmulatorKind kind)
    {
        EnsureDiscovery();
        var result = new List<EmulatorDescriptor>();
        foreach (EmulatorDescriptor desc in Descriptors)
        {
            if (desc.Kind == kind)
                result.Add(desc);
        }
        return result;
    }

    /// <summary>
    /// Returns every descriptor whose kind maps from <paramref name="platform"/> and whose
    /// folder exists on device (direct filesystem or through the file broker). Order matches
    /// the manifest; the return is an empty list when no descriptor resolves or when the
    /// platform is not one an emulator descriptor targets. Callers that must present every
    /// installed emulator for the platform (a selector prompt) use this method; callers that
    /// only need the first hit use <see cref="Resolve(EmulatorKind)"/>.
    /// </summary>
    public static IReadOnlyList<EmulatorDescriptor> ResolveAllForKind(GamePlatform platform)
    {
        EnsureDiscovery();
        var resolved = new List<EmulatorDescriptor>();
        if (!TryMapPlatform(platform, out EmulatorKind kind))
            return resolved;
        foreach (EmulatorDescriptor desc in Descriptors)
        {
            if (desc.Kind != kind)
                continue;
            if (ResolveFolder(desc) is not null)
                resolved.Add(desc);
        }
        return resolved;
    }

    /// <summary>
    /// Returns the first descriptor of the given kind whose folder exists on device, or null.
    /// Probes each candidate path in order until one is found.
    /// </summary>
    public static EmulatorDescriptor? Resolve(EmulatorKind kind)
    {
        EnsureDiscovery();
        foreach (EmulatorDescriptor desc in Descriptors)
        {
            if (desc.Kind != kind)
                continue;

            foreach (string path in desc.SourceFolderProbe)
            {
                if (FileSystem.IsDirectory(path))
                    return desc;
            }
        }
        // Broker fallback for paths on partitions the mount namespace does not bind.
        if (SandboxBroker.IsReachable())
        {
            foreach (EmulatorDescriptor desc in Descriptors)
            {
                if (desc.Kind != kind)
                    continue;
                foreach (string path in desc.SourceFolderProbe)
                {
                    if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsDirectory(path))
                        return desc;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the first existing probe path for a specific descriptor, or null.
    /// </summary>
    public static string? ResolveFolder(EmulatorDescriptor desc)
    {
        foreach (string path in desc.SourceFolderProbe)
        {
            if (FileSystem.IsDirectory(path))
                return path;
        }
        if (SandboxBroker.IsReachable())
        {
            foreach (string path in desc.SourceFolderProbe)
            {
                if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsDirectory(path))
                    return path;
            }
        }
        return null;
    }

    /// <summary>
    /// Reads the title id from an emulator folder's own <c>sce_sys/param.sfo</c>. Returns null
    /// when the folder does not carry an SFO, when the SFO does not carry a <c>TITLE_ID</c>
    /// entry, or when the read fails. Callers use this to override a manifest title id whose
    /// exact value does not match the on-device install, so a launch reaches the emulator that
    /// is actually installed instead of the one the manifest was written against.
    /// </summary>
    public static string? ReadTitleIdFromFolder(string folder)
    {
        if (string.IsNullOrEmpty(folder))
            return null;
        string sfoPath = folder.TrimEnd('/') + "/sce_sys/param.sfo";
        try
        {
            byte[]? bytes = null;
            if (SandboxBroker.IsOnBrokerPartition(sfoPath) && SandboxBroker.IsReachable())
            {
                var (outcome, data) = SandboxBroker.ReadAllBytes(sfoPath);
                if (outcome == BrokerOutcome.Ok && data.Length > 0)
                    bytes = data;
            }
            if (bytes is null)
            {
                if (!FileSystem.Exists(sfoPath))
                    return null;
                bytes = FileSystem.ReadAllBytes(sfoPath);
            }
            if (bytes is null || bytes.Length == 0)
                return null;

            SharpProspero.Storage.Sfo.SfoFile? sfo = SharpProspero.Storage.Sfo.SfoFile.Read(bytes);
            if (sfo is null)
                return null;
            string? titleId = sfo.GetString("TITLE_ID");
            return string.IsNullOrEmpty(titleId) ? null : titleId;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the resolved on-device folder path for the first descriptor of the given kind,
    /// or null if no candidate path exists.
    /// </summary>
    public static string? ResolveFolder(EmulatorKind kind)
    {
        EnsureDiscovery();
        foreach (EmulatorDescriptor desc in Descriptors)
        {
            if (desc.Kind != kind)
                continue;

            foreach (string path in desc.SourceFolderProbe)
            {
                if (FileSystem.IsDirectory(path))
                    return path;
            }
        }
        if (SandboxBroker.IsReachable())
        {
            foreach (EmulatorDescriptor desc in Descriptors)
            {
                if (desc.Kind != kind)
                    continue;
                foreach (string path in desc.SourceFolderProbe)
                {
                    if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsDirectory(path))
                        return path;
                }
            }
        }
        return null;
    }

    private static void LoadFromEmbeddedManifest()
    {
        Assembly assembly = typeof(EmulatorRegistry).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream("emulator-manifest.json");
        if (stream == null)
            return;

        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd();
        if (!JsonValue.TryParse(text, out JsonValue root))
            return;

        JsonValue emuArray = root["emulators"];
        if (emuArray.Type != JsonType.Array)
            return;

        for (int i = 0; i < emuArray.Count; i++)
        {
            JsonValue entry = emuArray[i];
            if (entry.Type != JsonType.Object)
                continue;

            string kindStr = entry.GetString("kind");
            if (!TryParseKind(kindStr, out EmulatorKind kind))
                continue;

            string name = entry.GetString("name");
            string titleId = entry.GetString("title_id");
            string contentId = entry.GetString("content_id");
            string folder = entry.GetString("folder");

            // Build the probe list: /data/homebrew/emulators/<folder>,
            // /mnt/usb0..7/homebrew/<folder>, and /app0/emulators/<folder>
            // as bundled fallback. Eight USB slots match the FreeBSD USB naming
            // range the kernel exposes to userland.
            var probes = new List<string>
            {
                "/data/homebrew/emulators/" + folder,
            };
            for (int u = 0; u < 8; u++)
                probes.Add("/mnt/usb" + u + "/homebrew/" + folder);
            probes.Add("/app0/emulators/" + folder);

            // If the manifest has a custom device_path, prepend it.
            string devicePath = entry.GetString("device_path");
            if (!string.IsNullOrEmpty(devicePath) && !probes.Contains(devicePath))
                probes.Insert(0, devicePath);

            Descriptors.Add(new EmulatorDescriptor(kind, name,
                titleId, contentId, probes));
        }
    }

    private static bool TryParseKind(string value, out EmulatorKind kind)
    {
        kind = default;
        if (string.Equals(value, "PS1", StringComparison.OrdinalIgnoreCase))
        {
            kind = EmulatorKind.PS1;
            return true;
        }
        if (string.Equals(value, "PS2", StringComparison.OrdinalIgnoreCase))
        {
            kind = EmulatorKind.PS2;
            return true;
        }
        if (string.Equals(value, "PSP", StringComparison.OrdinalIgnoreCase))
        {
            kind = EmulatorKind.PSP;
            return true;
        }
        return false;
    }

    // Maps a backup platform to the emulator kind that runs its content. Only PS1, PS2, and
    // PSP have on-device emulator descriptors; the other platforms return false so a caller
    // gets an empty resolved list instead of a fallback that would run the wrong emulator.
    private static bool TryMapPlatform(GamePlatform platform, out EmulatorKind kind)
    {
        switch (platform)
        {
            case GamePlatform.PS1:
                kind = EmulatorKind.PS1;
                return true;
            case GamePlatform.PS2:
                kind = EmulatorKind.PS2;
                return true;
            case GamePlatform.PSP:
                kind = EmulatorKind.PSP;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
