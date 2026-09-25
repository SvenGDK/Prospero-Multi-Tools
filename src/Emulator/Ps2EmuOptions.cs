// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Shell;
using SharpProspero.Storage;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;
using System.Text;

namespace ProsperoMultiTools.Emulator;

/// <summary>GS uprender resolution multiplier.</summary>
internal enum GsUprender
{
    Native,
    Render2x2,
    Render3x3,
    Render4x4,
}

/// <summary>GS upscaling filter.</summary>
internal enum GsUpscale
{
    None,
    EdgeSmooth,
    BilinearSharp,
    Sharp,
    XBRZ,
}

/// <summary>Host display mode.</summary>
internal enum HostDisplayMode
{
    Normal,
    Full,
    Zoom,
}

/// <summary>Multitap port configuration.</summary>
internal enum MultitapMode
{
    None,
    Port1,
    Port2,
    Both,
}

/// <summary>
/// Full option model for the PS2 emulator (Jakv2 / Roguev1).
/// Every property maps to a <c>--key=value</c> flag in <c>config-emu-ps4.txt</c>.
/// </summary>
internal sealed class Ps2EmuOptions : IEmuOptions
{
    // ---- Identity ----

    /// <summary>PS2 disc serial (e.g. "SLES-12345").</summary>
    public string Ps2TitleId { get; set; } = "SLES-00000";

    /// <summary>Maximum number of disc images (1..5).</summary>
    public int MaxDiscNum { get; set; } = 1;

    // ---- Graphics ----

    /// <summary>GS uprender resolution.</summary>
    public GsUprender GsUprender { get; set; } = GsUprender.Render2x2;

    /// <summary>GS upscaling filter.</summary>
    public GsUpscale GsUpscale { get; set; } = GsUpscale.EdgeSmooth;

    /// <summary>Host display mode.</summary>
    public HostDisplayMode HostDisplayMode { get; set; } = HostDisplayMode.Full;

    // ---- Controller ----

    /// <summary>Multitap adapter configuration.</summary>
    public MultitapMode MultitapMode { get; set; } = MultitapMode.None;

    // ---- Behavior ----

    /// <summary>Whether the emulator restarts on disc change.</summary>
    public bool RestartEmulatorOnDiscChange { get; set; } = true;

    /// <summary>Apply a widescreen memory-patch LUA script.</summary>
    public bool UseWidescreenPatch { get; set; }

    /// <summary>Enable the lopnor (PS3) compatibility config.</summary>
    public bool EnableLopnorConfig { get; set; }

    // ---- Speed optimizations ----

    /// <summary>
    /// When true, emits VU/COP2 optimization flags:
    /// <c>--vu0-opt-flags=1 --vu1-opt-flags=1 --cop2-opt-flags=1</c>,
    /// const-prop disabled, and newprog cache policies for jr/jalr.
    /// </summary>
    public bool ImproveSpeed { get; set; }

    // ---- Graphics fixes ----

    /// <summary>
    /// When true, emits FPU/VU/COP2 clamping flags to fix rendering glitches:
    /// <c>--fpu-no-clamping=0 --fpu-clamp-results=1</c> etc.
    /// </summary>
    public bool FixGraphics { get; set; }

    /// <summary>Disable multi-threaded VU processing (<c>--vu1=jit-sync</c>).</summary>
    public bool DisableMTVU { get; set; }

    /// <summary>Disable instant VIF1 transfer (<c>--vif1-instant-xfer=0</c>).</summary>
    public bool DisableInstantVIF1Transfer { get; set; }

    // ---- Audio ----

    /// <summary>Host audio output enabled (1) or disabled (0).</summary>
    public int HostAudio { get; set; } = 1;

    // ---- Paths ----

    /// <summary>VMC (virtual memory card) storage path.</summary>
    public string PathVmc { get; set; } = "/tmp/vmc";

    /// <summary>
    /// ROM firmware image filename. Reads and writes route through the lazy probe: an explicit
    /// value set by a caller or by the config parser is honoured, otherwise the getter globs the
    /// resolved emulator folder for <c>*.crack</c> and returns the first match. When neither an
    /// explicit value nor a probe match is available the fixed default (<see cref="DefaultRomFile"/>)
    /// is returned and <see cref="LastRomResolveNote"/> is set to a one-line description of the
    /// fallback so the launch screen can surface it.
    /// </summary>
    /// <remarks>
    /// The probe result is cached per emulator folder in <see cref="RomFileCache"/>; a later
    /// access for the same folder answers from the cache without touching the file system.
    /// </remarks>
    public string RomFile
    {
        get
        {
            if (_explicitRomFile is not null)
                return _explicitRomFile;
            string folder = EmulatorFolder;
            if (string.IsNullOrEmpty(folder))
                return DefaultRomFile;
            string resolved = ResolveRomFileForFolder(folder, out string? note);
            LastRomResolveNote = note;
            return resolved;
        }
        set => _explicitRomFile = value;
    }

    /// <summary>
    /// Fallback ROM filename used when neither a caller nor the probe finds a <c>*.crack</c> file
    /// in the resolved emulator folder.
    /// </summary>
    internal const string DefaultRomFile = "PS20220WD20050620.crack";

    /// <summary>
    /// Emulator folder the lazy <see cref="RomFile"/> probe globs for <c>*.crack</c>. The launch
    /// screen sets this once the folder has been resolved on device so the getter finds the ROM
    /// filename the runtime actually ships.
    /// </summary>
    internal string EmulatorFolder { get; set; } = "";

    /// <summary>
    /// Set by the <see cref="RomFile"/> getter each time the fallback fires so the launch screen
    /// can log the reason. Empty when the resolved value came from an explicit setter or from a
    /// successful probe.
    /// </summary>
    internal string? LastRomResolveNote { get; private set; }

    // Backing field for RomFile. Null means "not explicitly set" so the getter runs the folder
    // probe; a non-null value came from a caller or the config parser and is returned verbatim.
    private string? _explicitRomFile;

    // Folder -> resolved ROM filename ("" when the probe found no *.crack and the default is used).
    // Static so a re-launch of the same emulator folder answers from the cache without a broker or
    // file-system round-trip. Reads and writes hold the cache lock; entries are never invalidated
    // because the emulator folder's ROM file is fixed for the runtime the caller shipped.
    private static readonly Dictionary<string, string> RomFileCache = new(StringComparer.Ordinal);

    /// <summary>Verbose CDVD read logging (0 = off, 1 = on).</summary>
    public int VerboseCdvdReads { get; set; }

    // ---- Disc images ----

    /// <summary>Up to 5 disc image paths (relative to the emulator root, e.g. "image/disc01.iso").</summary>
    public List<string> ImagePathList { get; } = [];

    // ---- Extra config files ----

    /// <summary>Custom memory card file path (e.g. "feature_data/SLES-12345/custom.card").</summary>
    public string MemoryCardPath { get; set; } = "";

    /// <summary>Path to a per-game LUA configuration script.</summary>
    public string LuaConfigPath { get; set; } = "";

    /// <summary>Path to a per-game TXT configuration file.</summary>
    public string TxtConfigPath { get; set; } = "";

    // ---- Extended paths ----

    /// <summary>Emulator log output path.</summary>
    public string PathEmulog { get; set; } = "/tmp/recordings";

    /// <summary>Patches directory (e.g. "/app0/patches").</summary>
    public string PathPatches { get; set; } = "";

    /// <summary>Trophy data directory (e.g. "/app0/trophy_data").</summary>
    public string PathTrophyData { get; set; } = "";

    /// <summary>Feature data directory (e.g. "/app0/feature_data").</summary>
    public string PathFeatureData { get; set; } = "";

    /// <summary>Host OSD overlay (0 = off, 1 = on).</summary>
    public int HostOsd { get; set; }

    /// <summary>PS2 language override ("system" follows the console language).</summary>
    public string Ps2Lang { get; set; } = "system";

    /// <summary>Local LUA config path (empty string = none).</summary>
    public string ConfigLocalLua { get; set; } = "";

    /// <summary>
    /// Verbatim text a per-game database TXT config resolved to, appended to the emitted
    /// <c>config-emu-ps4.txt</c> under a header comment so the emulator picks up the flags on the
    /// same launch. Empty when no database TXT was resolved or when a user-supplied TXT already
    /// covers the game (the user file has priority and is appended by <c>EmitPs2</c>).
    /// </summary>
    public string DatabaseTxtContent { get; set; } = "";

    // ---- Helpers ----

    /// <summary>Returns the <c>--gs-uprender</c> flag value for the current setting.</summary>
    internal string GetUprenderValue()
    {
        return GsUprender switch
        {
            Emulator.GsUprender.Native => "native",
            Emulator.GsUprender.Render2x2 => "2x2",
            Emulator.GsUprender.Render3x3 => "3x3",
            Emulator.GsUprender.Render4x4 => "4x4",
            _ => "2x2",
        };
    }

    /// <summary>Returns the <c>--gs-upscale</c> flag value for the current setting.</summary>
    internal string GetUpscaleValue()
    {
        return GsUpscale switch
        {
            Emulator.GsUpscale.None => "none",
            Emulator.GsUpscale.EdgeSmooth => "EdgeSmooth",
            Emulator.GsUpscale.BilinearSharp => "BilinearSharp",
            Emulator.GsUpscale.Sharp => "Sharp",
            Emulator.GsUpscale.XBRZ => "xBRZ",
            _ => "EdgeSmooth",
        };
    }

    /// <summary>Returns the <c>--host-display-mode</c> flag value for the current setting.</summary>
    internal string GetDisplayModeValue()
    {
        return HostDisplayMode switch
        {
            Emulator.HostDisplayMode.Normal => "normal",
            Emulator.HostDisplayMode.Full => "full",
            Emulator.HostDisplayMode.Zoom => "zoom",
            _ => "full",
        };
    }

    // Globs <paramref name="folder"/> for a *.crack ROM firmware file and returns the first match,
    // or the fixed default when the folder cannot be listed or carries no *.crack. Reads route
    // through the sandbox broker for partitions the mount namespace does not bind (a folder under
    // /data, /user, /user2, /hdd); other partitions are listed direct. Results are cached in
    // <see cref="RomFileCache"/> so a subsequent access for the same folder skips both routes.
    private static string ResolveRomFileForFolder(string folder, out string? note)
    {
        note = null;
        string key = folder.TrimEnd('/');
        lock (RomFileCache)
        {
            if (RomFileCache.TryGetValue(key, out string? cached))
            {
                if (cached.Length == 0)
                    note = "No *.crack ROM found in " + key + "; using bundled default " + DefaultRomFile + ".";
                return cached.Length == 0 ? DefaultRomFile : cached;
            }
        }

        string? found = TryFindCrackFile(key);
        lock (RomFileCache)
            RomFileCache[key] = found ?? "";

        if (found is null)
        {
            note = "No *.crack ROM found in " + key + "; using bundled default " + DefaultRomFile + ".";
            return DefaultRomFile;
        }
        return found;
    }

    // Returns the first *.crack filename in <paramref name="folder"/>, case-insensitive. Prefers
    // the direct file-system listing and falls back to the broker for paths on partitions the
    // module cannot open directly. Returns null when neither route yields a match, when the
    // folder does not exist, or when both routes refuse the listing.
    private static string? TryFindCrackFile(string folder)
    {
        if (string.IsNullOrEmpty(folder))
            return null;

        // Direct enumeration first; a bound partition (/mnt/usb0, /app0, ...) answers here without
        // a broker round-trip. TryEnumerateDirectory reports the failure code rather than throwing
        // on a partition the module does not bind, so we can fall through to the broker route.
        if (FileSystem.TryEnumerateDirectory(folder, out IReadOnlyList<DirectoryEntry> entries, out _))
        {
            foreach (DirectoryEntry entry in entries)
            {
                if (entry.IsFile && HasCrackExtension(entry.Name))
                    return entry.Name;
            }
        }

        // Broker fallback for paths on partitions the mount namespace does not bind. The daemon
        // has to be reachable; otherwise no *.crack can be discovered and the caller falls back
        // to the bundled default.
        if (SandboxBroker.IsOnBrokerPartition(folder) && SandboxBroker.IsReachable())
        {
            var (outcome, brokerEntries) = SandboxBroker.List(folder);
            if (outcome == BrokerOutcome.Ok)
            {
                foreach (BrokerDirEntry entry in brokerEntries)
                {
                    if (entry.IsFile && HasCrackExtension(entry.Name))
                        return entry.Name;
                }
            }
        }
        return null;
    }

    private static bool HasCrackExtension(string name) =>
        name.EndsWith(".crack", StringComparison.OrdinalIgnoreCase);

    // ---- IEmuOptions ----

    void IEmuOptions.EmitConfig(StringBuilder builder) => builder.Append(ConfigFileEmitter.EmitPs2(this));

    void IEmuOptions.BuildOptionRows(ScrollMenu menu, MultiToolsShell shell, Action rebuild)
    {
        menu.Add(new Label("Graphics") { TextColor = shell.Theme.Accent });

        string[] uprenderLabels = ["Native", "2x2", "3x3", "4x4"];
        menu.Add(new OptionSelector("Uprender", uprenderLabels, (int)GsUprender, i =>
            GsUprender = (GsUprender)i));

        string[] upscaleLabels = ["None", "EdgeSmooth", "BilinearSharp", "Sharp", "xBRZ"];
        menu.Add(new OptionSelector("Upscale filter", upscaleLabels, (int)GsUpscale, i =>
            GsUpscale = (GsUpscale)i));

        string[] displayLabels = ["Normal", "Full", "Zoom"];
        menu.Add(new OptionSelector("Display mode", displayLabels, (int)HostDisplayMode, i =>
            HostDisplayMode = (HostDisplayMode)i));

        menu.Add(new Separator());
        menu.Add(new Label("Controller") { TextColor = shell.Theme.Accent });

        string[] multitapLabels = ["None", "Port 1", "Port 2", "Both"];
        menu.Add(new OptionSelector("Multitap", multitapLabels, (int)MultitapMode, i =>
            MultitapMode = (MultitapMode)i));

        menu.Add(new Separator());
        menu.Add(new Label("Behavior") { TextColor = shell.Theme.Accent });

        menu.Add(new Checkbox("Restart on disc change", RestartEmulatorOnDiscChange, v =>
            RestartEmulatorOnDiscChange = v));
        menu.Add(new Checkbox("Widescreen patch", UseWidescreenPatch, v =>
            UseWidescreenPatch = v));
        menu.Add(new Checkbox("PS3 compatibility config", EnableLopnorConfig, v =>
            EnableLopnorConfig = v));

        menu.Add(new Separator());
        menu.Add(new Label("Performance") { TextColor = shell.Theme.Accent });

        menu.Add(new Checkbox("VU/COP2 speed optimizations", ImproveSpeed, v =>
            ImproveSpeed = v));
        menu.Add(new Checkbox("FPU/VU clamping fixes", FixGraphics, v =>
            FixGraphics = v));
        menu.Add(new Checkbox("Disable MTVU", DisableMTVU, v =>
            DisableMTVU = v));
        menu.Add(new Checkbox("Disable instant VIF1 transfer", DisableInstantVIF1Transfer, v =>
            DisableInstantVIF1Transfer = v));

        menu.Add(new Separator());
        menu.Add(new Label("Audio") { TextColor = shell.Theme.Accent });

        menu.Add(new Checkbox("Host audio", HostAudio != 0, v =>
            HostAudio = v ? 1 : 0));

        menu.Add(new Separator());
        menu.Add(new Label("Disc") { TextColor = shell.Theme.Accent });

        menu.Add(new Stepper("Max disc count", MaxDiscNum, 1, 5, changed: v =>
            MaxDiscNum = (int)v));

        menu.Add(new Separator());
        menu.Add(new Label("Paths") { TextColor = shell.Theme.Accent });

        menu.Add(new Button($"VMC: {PathVmc}", () =>
            FilePickerScreen.PickFolder(shell, "Pick a VMC folder",
                Places.StartingPoint(shell.Settings.StartPath) ?? PathVmc,
                v => { if (!string.IsNullOrEmpty(v)) { PathVmc = v; rebuild(); } })));

        menu.Add(new Button($"Emulog: {PathEmulog}", () =>
            FilePickerScreen.PickFolder(shell, "Pick an emulog folder",
                Places.StartingPoint(shell.Settings.StartPath) ?? PathEmulog,
                v => { if (!string.IsNullOrEmpty(v)) { PathEmulog = v; rebuild(); } })));
    }
}
