// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using SharpProspero.Interop;
using SharpProspero.Storage;
using System;
using System.Collections.Generic;

namespace ProsperoMultiTools.Shell;

internal readonly record struct Place(string Path, string Name, string Description, bool Writable);

internal readonly record struct PlaceProbe(Place Place, bool Readable, int ErrorCode, string? OverrideReason = null, bool ViaBroker = false)
{
    public string Reason
    {
        get
        {
            if (Readable)
                return string.Empty;
            if (!string.IsNullOrEmpty(OverrideReason))
                return OverrideReason;
            return SceResult.Describe(ErrorCode);
        }
    }
}

/// <summary>
/// The known places on the console's file system, grouped by whether the running process reaches
/// them from inside its own fpkg sandbox or only after the escalation daemon has widened the file
/// view. A caller runs <see cref="Configure"/> once the escalation outcome is in hand, then calls
/// <see cref="Refresh"/> to probe each candidate.
/// </summary>
internal static class Places
{
    // Reachable inside every fpkg sandbox because they are bound in at mount time; the process's
    // own root-directory vnode does not have to be widened for these to answer.
    private static readonly Place[] SandboxCandidates =
    [
        new("/mnt/usb0",   "USB 0",             "First USB storage device.",     true),
        new("/mnt/usb1",   "USB 1",             "Second USB storage device.",    true),
        new("/mnt/usb2",   "USB 2",             "Third USB storage device.",     true),
        new("/mnt/usb3",   "USB 3",             "Fourth USB storage device.",    true),
        new("/mnt/usb4",   "USB 4",             "Fifth USB storage device.",     true),
        new("/mnt/usb5",   "USB 5",             "Sixth USB storage device.",     true),
        new("/mnt/usb6",   "USB 6",             "Seventh USB storage device.",   true),
        new("/mnt/usb7",   "USB 7",             "Eighth USB storage device.",    true),
        new("/mnt/disc",   "Disc",              "Mounted disc content.",         false),
    ];

    // Only reachable when the escalation daemon has lifted the process's root-directory vnode out
    // of the sandbox. Every one of these entries is a real root of the console (verified against
    // the console's own root listing) that the sandbox does not bind in.
    private static readonly Place[] UnjailedCandidates =
    [
        new("/data",       "Application data",  "Writable application storage.", true),
        new("/user",       "User data",         "Per-user files.",               true),
        new("/user2",      "User data (spare)", "Secondary per-user files.",     true),
        new("/hdd",        "Hard drive",        "The console's hard-drive root.",true),
        new("/hostapp",    "Host application",  "Host-mapped application.",      false),
        new("/system",     "System",            "System files.",                 false),
        new("/system_ex",  "System extras",     "Installed applications.",       false),
        new("/system_data","System data",       "System data partition.",        false),
        new("/preinst",    "Pre-installed",     "Pre-installed content.",        false),
        new("/update",     "Update",            "System update payload.",        false),
        new("/",           "Root",              "The filesystem root.",          false),
    ];

    private static UnjailResult _unjail;
    private static List<PlaceProbe> _probed = [];
    private static List<PlaceProbe> _reachable = [];
    private static List<PlaceProbe> _unreachable = [];

    public static IReadOnlyList<PlaceProbe> Probed => _probed;
    public static IReadOnlyList<PlaceProbe> Reachable => _reachable;
    public static IReadOnlyList<PlaceProbe> Unreachable => _unreachable;

    /// <summary>True when the escalation daemon reported the widened view as applied.</summary>
    public static bool IsUnjailApplied => _unjail.Applied;

    /// <summary>The reason the daemon gave, when it did not apply the widened view.</summary>
    public static string UnjailReason => _unjail.Reason;

    /// <summary>
    /// Records the outcome of the escalation handshake so <see cref="Refresh"/> knows whether the
    /// entries in the second group are worth probing at all.
    /// </summary>
    public static void Configure(UnjailResult unjail) => _unjail = unjail;

    /// <summary>
    /// Probes every known place. Sandbox-visible candidates are always probed; entries that only
    /// the widened view reaches are probed when the escalation daemon reported it as applied, and
    /// otherwise recorded as unreachable with the daemon's own reason so the diagnostics screen can
    /// tell the user why they are missing.
    /// </summary>
    public static void Refresh()
    {
        int total = SandboxCandidates.Length + UnjailedCandidates.Length;
        var probed = new List<PlaceProbe>(total);
        var reachable = new List<PlaceProbe>(total);
        var unreachable = new List<PlaceProbe>(total);

        foreach (Place place in SandboxCandidates)
        {
            PlaceProbe probe = Probe(place);
            probed.Add(probe);
            if (probe.Readable)
                reachable.Add(probe);
            else
                unreachable.Add(probe);
        }

        foreach (Place place in UnjailedCandidates)
        {
            PlaceProbe probe;
            if (_unjail.Applied)
            {
                probe = Probe(place);
            }
            else
            {
                // The place exists on the console; the process just does not have a view of it.
                // Report that plainly instead of the raw errno cascade a probe would produce.
                string reason = string.IsNullOrEmpty(_unjail.Reason)
                    ? "The escalation daemon has not widened the file view."
                    : $"The escalation daemon has not widened the file view: {_unjail.Reason}";
                probe = new PlaceProbe(place, Readable: false, ErrorCode: 0, OverrideReason: reason);
            }
            probed.Add(probe);
            if (probe.Readable)
                reachable.Add(probe);
            else
                unreachable.Add(probe);
        }

        _probed = probed;
        _reachable = reachable;
        _unreachable = unreachable;
    }

    private static PlaceProbe Probe(Place place)
    {
        bool ok;
        int error;
        try
        {
            ok = FileSystem.TryEnumerateDirectory(place.Path, out _, out error);
        }
        catch
        {
            ok = false;
            error = -1;
        }
        // A direct enumeration that fails on one of the daemon-visible partitions still leaves
        // the folder reachable when the file broker is up - the daemon runs outside the caller's
        // sandbox and can stat the same path. Consult the broker as a second opinion so /data
        // and /user do not read as unreachable on a session where only the direct syscall was
        // refused. Only partitions the module's mount namespace does not bind fall back to the
        // broker; every other place reads its direct probe result as is.
        if (!ok
            && SandboxBroker.IsOnBrokerPartition(place.Path)
            && SandboxBroker.IsReachable()
            && SandboxBroker.IsDirectory(place.Path))
            return new PlaceProbe(place, Readable: true, ErrorCode: 0, ViaBroker: true);
        return new PlaceProbe(place, ok, ok ? 0 : error);
    }

    public static string? StartingPoint(string preferred)
    {
        if (!string.IsNullOrEmpty(preferred))
        {
            try
            {
                if (FileSystem.TryEnumerateDirectory(preferred, out _, out _))
                    return preferred;
            }
            catch
            {
            }
        }

        foreach (PlaceProbe probe in _reachable)
        {
            if (probe.Readable)
                return probe.Place.Path;
        }
        return null;
    }

    public static string? DataFolder(string folderName)
    {
        // The folder name is a single path segment placed under one of the writable roots.
        // A caller passing "..", "/absolute/path", or "sub/dir" would either escape the root
        // (PathUtil.Combine returns an absolute right-hand-side unchanged) or land in a
        // nested directory the caller never named. Reject any such input up front so both
        // the in-sandbox branch and the broker-fallback branch operate on the same well-
        // formed segment.
        string sanitized = SanitizeDataFolderName(folderName);
        if (sanitized.Length == 0)
            return null;

        foreach (PlaceProbe probe in _reachable)
        {
            if (!probe.Place.Writable)
                continue;
            string path = PathUtil.Combine(probe.Place.Path, sanitized);
            try
            {
                FileSystem.CreateDirectoryRecursive(path);
                string test = PathUtil.Combine(path, ".probe");
                FileSystem.WriteAllBytes(test, [(byte)'t']);
                FileSystem.DeleteFile(test);
                return path;
            }
            catch
            {
            }
        }

        // A writable in-sandbox folder was not available. When the file broker is running the
        // caller's data folder can still land on /data under the daemon's namespace; the shell's
        // storage helpers know to route reads and writes at that path through the broker.
        if (SandboxBroker.IsReachable())
        {
            string brokerPath = "/data/" + sanitized;
            if (SandboxBroker.MkdirRecursive(brokerPath) == BrokerOutcome.Ok)
                return brokerPath;
        }
        return null;
    }

    // Rejects a folder name that would break out of /data or wander into a sibling partition.
    // Slashes, backslashes, dot-only segments, and NUL characters are stripped; the result is
    // treated as the single path segment placed under /data.
    private static string SanitizeDataFolderName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;
        Span<char> buffer = stackalloc char[name.Length];
        int written = 0;
        foreach (char c in name)
        {
            if (c == '/' || c == '\\' || c == '\0')
                continue;
            buffer[written++] = c;
        }
        string clean = new string(buffer[..written]);
        return clean is "." or ".." ? string.Empty : clean;
    }

    public static IReadOnlyList<string> UsbPaths()
    {
        var paths = new List<string>();
        for (int i = 0; i <= 7; i++)
        {
            string path = $"/mnt/usb{i}";
            try
            {
                if (FileSystem.TryEnumerateDirectory(path, out _, out _))
                    paths.Add(path);
            }
            catch
            {
            }
        }
        return paths;
    }
}
