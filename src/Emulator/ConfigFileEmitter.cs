// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Shell;
using SharpProspero.Storage;
using System;
using System.IO;
using System.Text;

namespace ProsperoMultiTools.Emulator;

/// <summary>
/// Emitter for the two config files a per-game launch drops next to an emulator's own eboot:
/// <c>config-emu-ps4.txt</c> for PS2 (CRLF), <c>config-title.txt</c> for PS1 (LF) and PSP (CRLF).
/// Each flag is one <c>--key=value</c> line; comment lines start with <c>#</c>.
/// </summary>
internal static class ConfigFileEmitter
{
    // PS2 and PSP config lines end with CRLF; PS1 uses LF. Each newline choice matches the format
    // the corresponding emulator's shipped example config uses on device.
    private const string Ps2Newline = "\r\n";
    private const string Ps1Newline = "\n";
    private const string PspNewline = "\r\n";

    /// <summary>
    /// Serializes <paramref name="options"/> and writes the result to the appropriate
    /// config file in <paramref name="folder"/>. PS2 options produce
    /// <c>config-emu-ps4.txt</c>; PS1 and PSP produce <c>config-title.txt</c>.
    /// </summary>
    public static void WriteConfig(string folder, IEmuOptions options)
    {
        var builder = new StringBuilder(512);
        options.EmitConfig(builder);
        string fileName = options is Ps2EmuOptions ? "config-emu-ps4.txt" : "config-title.txt";
        string path = folder.TrimEnd('/') + "/" + fileName;
        try
        {
            FileSystem.WriteAllText(path, builder.ToString());
            return;
        }
        catch
        {
            if (!SandboxBroker.IsOnBrokerPartition(path) || !SandboxBroker.IsReachable())
                throw;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
        BrokerOutcome outcome = SandboxBroker.WriteAllBytes(path, bytes);
        if (outcome != BrokerOutcome.Ok)
            throw new IOException("Broker write failed for " + path + ": " + outcome);
    }

    // -------------------------------------------------------------------
    //  PS2 - config-emu-ps4.txt (CRLF)
    // -------------------------------------------------------------------

    /// <summary>
    /// Emits the <c>config-emu-ps4.txt</c> content for a PS2 emulator. The ten base flags
    /// come first (in the documented order), followed by the optional per-game blocks:
    /// disc-change suppression, multitap, PS3 compatibility, widescreen trophy-data path,
    /// LUA patches path, and the emulator-fix flags (speed, clamping, MTVU, VIF1). Per-game
    /// additions are gated on the caller opting in, so the shape of the base file stays
    /// stable across every backup.
    /// </summary>
    public static string EmitPs2(Ps2EmuOptions opts)
    {
        var sb = new StringBuilder();

        // The 13 base flags in the exact order and shape every shipped emulator's own
        // config-emu-ps4.txt uses. Every one of them is unconditional: the shipped emulators
        // parse this file at boot and set defaults from what they read; a missing key on a
        // launch that skipped this file lands on a garbage default the emulator has never been
        // tested against.
        //
        //   --path-vmc="/tmp/vmc"
        //   --path-emulog="/tmp/recordings"
        //   --path-patches="/app0/patches"
        //   --path-trophydata="/app0/trophy_data"
        //   --path-featuredata="/app0/feature_data"
        //   --host-osd=0
        //   --host-audio=1
        //   --host-display-mode=full
        //   --ps2-title-id=<id>
        //   --ps2-lang=system
        //   --gs-uprender=2x2
        //   --gs-upscale=EdgeSmooth
        //   --rom="PS20220WD20050620.crack"
        AppendKvQuoted(sb, "path-vmc", opts.PathVmc, Ps2Newline);
        AppendKvQuoted(sb, "path-emulog", opts.PathEmulog, Ps2Newline);
        AppendKvQuoted(sb, "path-patches", string.IsNullOrEmpty(opts.PathPatches) ? "/app0/patches" : opts.PathPatches, Ps2Newline);
        AppendKvQuoted(sb, "path-trophydata", string.IsNullOrEmpty(opts.PathTrophyData) ? "/app0/trophy_data" : opts.PathTrophyData, Ps2Newline);
        AppendKvQuoted(sb, "path-featuredata", string.IsNullOrEmpty(opts.PathFeatureData) ? "/app0/feature_data" : opts.PathFeatureData, Ps2Newline);
        AppendKv(sb, "host-osd", opts.HostOsd.ToString(), Ps2Newline);
        AppendKv(sb, "host-audio", opts.HostAudio.ToString(), Ps2Newline);
        AppendKv(sb, "host-display-mode", opts.GetDisplayModeValue(), Ps2Newline);
        AppendKv(sb, "ps2-title-id", opts.Ps2TitleId, Ps2Newline);
        AppendKv(sb, "ps2-lang", string.IsNullOrEmpty(opts.Ps2Lang) ? "system" : opts.Ps2Lang, Ps2Newline);
        AppendKv(sb, "gs-uprender", opts.GetUprenderValue(), Ps2Newline);
        AppendKv(sb, "gs-upscale", opts.GetUpscaleValue(), Ps2Newline);
        AppendKvQuoted(sb, "rom", opts.RomFile, Ps2Newline);

        // Optional extra keys the shipped default omits, appended only when the caller opted in.
        // Kept out of the base block above so an unchanged launch produces a config the shipped
        // emulator sees the same shape it ships.
        if (opts.MaxDiscNum != 1)
            AppendKv(sb, "max-disc-num", opts.MaxDiscNum.ToString(), Ps2Newline);
        if (opts.VerboseCdvdReads != 0)
            AppendKv(sb, "verbose-cdvd-reads", opts.VerboseCdvdReads.ToString(), Ps2Newline);
        if (!string.IsNullOrEmpty(opts.ConfigLocalLua))
            AppendKvQuoted(sb, "config-local-lua", opts.ConfigLocalLua, Ps2Newline);

        // Disc-change reset override: emitted only when the caller has opted out of the default
        // emulator-reset-on-disc-change behavior.
        if (!opts.RestartEmulatorOnDiscChange)
        {
            sb.Append("#Disable emu reset on disc change").Append(Ps2Newline);
            AppendKv(sb, "switch-disc-reset", "0", Ps2Newline);
        }

        // Multitap adapter: one or both ports armed to "always" so the emulator surfaces the
        // extra controller slots at boot.
        switch (opts.MultitapMode)
        {
            case MultitapMode.Port1:
                sb.Append('#').Append("Enable Multitap").Append(Ps2Newline);
                AppendKv(sb, "mtap1", "always", Ps2Newline);
                break;
            case MultitapMode.Port2:
                sb.Append('#').Append("Enable Multitap").Append(Ps2Newline);
                AppendKv(sb, "mtap2", "always", Ps2Newline);
                break;
            case MultitapMode.Both:
                sb.Append('#').Append("Enable Multitap").Append(Ps2Newline);
                AppendKv(sb, "mtap1", "always", Ps2Newline);
                AppendKv(sb, "mtap2", "always", Ps2Newline);
                break;
        }

        // PS3 compatibility config: turns on the emulator's lopnor path so the packaged
        // per-game .cfgbin is honoured.
        if (opts.EnableLopnorConfig)
            AppendKv(sb, "lopnor-config", "1", Ps2Newline);

        // Per-game database-resolved TXT config, appended verbatim after a header comment. This
        // path fires when no user-supplied TXT is set on the options but a matching database
        // entry was resolved by <c>EmulatorLaunchScreen.WritePs2ExtraConfigs</c> and stored on
        // the options. The user-supplied TXT is appended by <see cref="AppendUserTxtConfig"/>
        // below; both paths land in the same file so the emulator picks up both sets of flags on
        // one launch.
        if (!string.IsNullOrEmpty(opts.DatabaseTxtContent))
        {
            sb.Append("#Database imported config").Append(Ps2Newline);
            sb.Append(opts.DatabaseTxtContent);
            if (!opts.DatabaseTxtContent.EndsWith("\n", StringComparison.Ordinal))
                sb.Append(Ps2Newline);
        }

        // Emulator fixes: speed (9 flags), clamping (8 flags), MTVU, VIF1. Order matches the
        // documented on-device format so the emulator parses each block in the expected sequence.
        if (opts.ImproveSpeed)
        {
            sb.Append('#').Append("Improve Speed").Append(Ps2Newline);
            AppendKv(sb, "vu0-opt-flags", "1", Ps2Newline);
            AppendKv(sb, "vu1-opt-flags", "1", Ps2Newline);
            AppendKv(sb, "cop2-opt-flags", "1", Ps2Newline);
            AppendKv(sb, "vu0-const-prop", "0", Ps2Newline);
            AppendKv(sb, "vu1-const-prop", "0", Ps2Newline);
            AppendKv(sb, "vu1-jr-cache-policy", "newprog", Ps2Newline);
            AppendKv(sb, "vu1-jalr-cache-policy", "newprog", Ps2Newline);
            AppendKv(sb, "vu0-jr-cache-policy", "newprog", Ps2Newline);
            AppendKv(sb, "vu0-jalr-cache-policy", "newprog", Ps2Newline);
        }

        if (opts.FixGraphics)
        {
            sb.Append('#').Append("Fix Graphics").Append(Ps2Newline);
            AppendKv(sb, "fpu-no-clamping", "0", Ps2Newline);
            AppendKv(sb, "fpu-clamp-results", "1", Ps2Newline);
            AppendKv(sb, "vu0-no-clamping", "0", Ps2Newline);
            AppendKv(sb, "vu0-clamp-results", "1", Ps2Newline);
            AppendKv(sb, "vu1-no-clamping", "0", Ps2Newline);
            AppendKv(sb, "vu1-clamp-results", "1", Ps2Newline);
            AppendKv(sb, "cop2-no-clamping", "0", Ps2Newline);
            AppendKv(sb, "cop2-clamp-results", "1", Ps2Newline);
        }

        if (opts.DisableMTVU)
        {
            sb.Append('#').Append("Disable MTVU").Append(Ps2Newline);
            AppendKv(sb, "vu1", "jit-sync", Ps2Newline);
        }

        if (opts.DisableInstantVIF1Transfer)
        {
            sb.Append('#').Append("Disable Instant VIF1 Transfer").Append(Ps2Newline);
            AppendKv(sb, "vif1-instant-xfer", "0", Ps2Newline);
        }

        // User TXT config: appended verbatim under a header comment when a path is set and
        // the file is readable. Placed after the fixes blocks so any user override lines
        // land last in the file, matching PS1 and PSP ordering.
        AppendUserTxtConfig(sb, opts.TxtConfigPath, Ps2Newline);

        return sb.ToString();
    }

    private static void AppendKv(StringBuilder sb, string key, string value, string newline)
        => sb.Append("--").Append(key).Append('=').Append(value).Append(newline);

    private static void AppendKvQuoted(StringBuilder sb, string key, string value, string newline)
        => sb.Append("--").Append(key).Append("=\"").Append(value).Append('"').Append(newline);

    // -------------------------------------------------------------------
    //  PS1 - config-title.txt (LF)
    // -------------------------------------------------------------------

    /// <summary>
    /// Emits the <c>config-title.txt</c> content for a PS1 emulator. Trophy suppression
    /// comes first so the emulator disables all trophy hooks before touching disc images
    /// or per-title state. Optional per-game overrides (BIOS chrome, gun peripheral,
    /// refresh override, analog pad) follow the render scale. A user-supplied TXT config
    /// is appended verbatim under a header comment when a path is set on the options.
    /// </summary>
    public static string EmitPs1(Ps1EmuOptions opts)
    {
        var sb = new StringBuilder();

        // Header comment names the disc serial for readers who inspect the file directly.
        if (!string.IsNullOrEmpty(opts.Ps1TitleId))
            sb.Append("# ").Append(opts.Ps1TitleId).Append(Ps1Newline);

        // Trophy suppression must precede image loading so the emulator disables the trophy
        // hooks before it registers any per-title callbacks.
        AppendKv(sb, "ps4-trophies", opts.Ps4Trophies.ToString(), Ps1Newline);
        AppendKv(sb, "ps5-uds", opts.Ps5Uds.ToString(), Ps1Newline);
        AppendKv(sb, "trophies", opts.Trophies.ToString(), Ps1Newline);

        // Disc images: emit one --image line per disc. A single-disc backup defaults to
        // data/disc1.bin; multi-disc backups produce data/disc1.bin, data/disc2.bin, ...
        // in the order the caller supplied them.
        if (opts.DiscPaths.Count == 0)
            AppendKvQuoted(sb, "image", "data/disc1.bin", Ps1Newline);
        else
            foreach (string disc in opts.DiscPaths)
                AppendKvQuoted(sb, "image", disc, Ps1Newline);

        // LibCrypt sub-channel value: emitted only when the disc has copy protection.
        if (!string.IsNullOrEmpty(opts.LibCrypt))
            AppendKv(sb, "libcrypt", opts.LibCrypt, Ps1Newline);

        // LUA patcher binding: the emulator locates per-title scripts by title id only
        // when a LUA config has been dropped alongside.
        if (!string.IsNullOrEmpty(opts.Ps1TitleId) && !string.IsNullOrEmpty(opts.LuaConfigPath))
            AppendKv(sb, "ps1-title-id", opts.Ps1TitleId, Ps1Newline);

        // Render scale is emitted unconditionally so the file always names the value the
        // emulator should render at.
        AppendKv(sb, "scale", opts.Scale.ToString(), Ps1Newline);

        // Optional per-game overrides in the documented order: BIOS chrome, gun peripheral,
        // refresh override, analog pad emulation.
        if (opts.BiosHideSceOsd)
            AppendKv(sb, "bios-hide-sce-osd", "1", Ps1Newline);
        if (opts.EnableGuncon)
            sb.Append("--guncon").Append(Ps1Newline);
        if (opts.Force60Hz)
            AppendKv(sb, "gpu-scanout-fps-override", "60", Ps1Newline);
        if (opts.SimAnalogPad != 0)
            AppendKv(sb, "sim-analog-pad", "0x" + opts.SimAnalogPad.ToString("X4"), Ps1Newline);

        // User TXT config: appended verbatim under a header comment when a path is set and
        // the file is readable. The read routes through the sandbox broker when needed.
        AppendUserTxtConfig(sb, opts.TxtConfigPath, Ps1Newline);

        return sb.ToString();
    }

    // -------------------------------------------------------------------
    //  PSP - config-title.txt (CRLF)
    // -------------------------------------------------------------------

    /// <summary>
    /// Emits the <c>config-title.txt</c> content for a PSP emulator. Trophy suppression
    /// comes first so the emulator disables all trophy hooks before loading the UMD image;
    /// the image path, anti-aliasing, multi-save toggle, and no-trophies flag follow. A
    /// user-supplied TXT config is appended verbatim under a header comment when a path
    /// is set on the options.
    /// </summary>
    public static string EmitPsp(PspEmuOptions opts)
    {
        var sb = new StringBuilder();

        // Trophy suppression must precede image loading so the emulator disables the trophy
        // hooks before it registers any UMD callbacks.
        AppendKv(sb, "ps4-trophies", opts.Ps4Trophies.ToString(), PspNewline);
        AppendKv(sb, "ps5-uds", opts.Ps5Uds.ToString(), PspNewline);
        AppendKv(sb, "trophies", opts.Trophies.ToString(), PspNewline);

        // UMD image path (default: data/USER_L0.IMG) followed by the fixed AA / multi-save /
        // no-trophies flags. Every flag is emitted every time so the file's shape stays stable.
        AppendKvQuoted(sb, "image", string.IsNullOrEmpty(opts.ImagePath) ? "data/USER_L0.IMG" : opts.ImagePath, PspNewline);
        AppendKv(sb, "antialias", opts.GetAntiAliasValue(), PspNewline);
        AppendKv(sb, "multisaves", opts.MultiSaves ? "true" : "false", PspNewline);
        AppendKv(sb, "notrophies", opts.NoTrophies ? "true" : "false", PspNewline);

        // User TXT config: appended verbatim under a header comment when a path is set and
        // the file is readable. The read routes through the sandbox broker when needed.
        AppendUserTxtConfig(sb, opts.TxtConfigPath, PspNewline);

        // Trailing blank line: every shipped example ends with an empty CRLF-terminated line
        // after the last flag, so the emitter matches that shape.
        sb.Append(PspNewline);

        return sb.ToString();
    }

    // Reads a per-game TXT config from disk and appends its contents under a
    // <c>#User imported config</c> header comment. The read routes through the sandbox
    // broker for partitions the mount namespace does not bind. A missing, unreachable,
    // or empty file is treated as no user config and produces no output.
    private static void AppendUserTxtConfig(StringBuilder sb, string path, string newline)
    {
        if (string.IsNullOrEmpty(path))
            return;
        string? content = ReadTextFileWithBroker(path);
        if (string.IsNullOrEmpty(content))
            return;
        sb.Append("#User imported config").Append(newline);
        sb.Append(content);
    }

    // Reads a text file, falling back to the sandbox broker for paths the mount namespace
    // does not bind. Returns null when the file cannot be reached or read.
    private static string? ReadTextFileWithBroker(string path)
    {
        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
        {
            var (outcome, data) = SandboxBroker.ReadAllBytes(path);
            if (outcome != BrokerOutcome.Ok || data.Length == 0)
                return null;
            return Encoding.UTF8.GetString(data);
        }
        try
        {
            if (!FileSystem.Exists(path))
                return null;
            byte[] bytes = FileSystem.ReadAllBytes(path);
            if (bytes.Length == 0)
                return null;
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    // -------------------------------------------------------------------
    //  Round-trip parser (config-emu-ps4.txt -> Ps2EmuOptions)
    // -------------------------------------------------------------------

    /// <summary>
    /// Parses a <c>config-emu-ps4.txt</c> into a <see cref="Ps2EmuOptions"/>.
    /// Used for round-trip verification.
    /// </summary>
    public static Ps2EmuOptions ParsePs2(string content)
    {
        var opts = new Ps2EmuOptions();
        bool hasMtap1 = false;
        bool hasMtap2 = false;

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (!line.StartsWith("--", StringComparison.Ordinal))
                continue;

            int eq = line.IndexOf('=');
            if (eq < 0)
                continue;

            string key = line.Substring(2, eq - 2);
            string val = line.Substring(eq + 1).Trim('"');

            switch (key)
            {
                case "path-vmc": opts.PathVmc = val; break;
                case "config-local-lua": opts.ConfigLocalLua = val; break;
                case "ps2-title-id": opts.Ps2TitleId = val; break;
                case "max-disc-num": _ = int.TryParse(val, out int md) ? opts.MaxDiscNum = md : 1; break;
                case "gs-uprender":
                    opts.GsUprender = val switch
                    {
                        "native" => GsUprender.Native,
                        "2x2" => GsUprender.Render2x2,
                        "3x3" => GsUprender.Render3x3,
                        "4x4" => GsUprender.Render4x4,
                        _ => GsUprender.Render2x2,
                    };
                    break;
                case "gs-upscale":
                    opts.GsUpscale = val switch
                    {
                        "none" => GsUpscale.None,
                        "EdgeSmooth" => GsUpscale.EdgeSmooth,
                        "BilinearSharp" => GsUpscale.BilinearSharp,
                        "Sharp" => GsUpscale.Sharp,
                        "xBRZ" => GsUpscale.XBRZ,
                        _ => GsUpscale.EdgeSmooth,
                    };
                    break;
                case "host-audio": _ = int.TryParse(val, out int ha) ? opts.HostAudio = ha : 1; break;
                case "rom": opts.RomFile = val; break;
                case "verbose-cdvd-reads": _ = int.TryParse(val, out int vc) ? opts.VerboseCdvdReads = vc : 0; break;
                case "host-display-mode":
                    opts.HostDisplayMode = val switch
                    {
                        "normal" => HostDisplayMode.Normal,
                        "full" => HostDisplayMode.Full,
                        "zoom" => HostDisplayMode.Zoom,
                        _ => HostDisplayMode.Full,
                    };
                    break;
                case "switch-disc-reset":
                    opts.RestartEmulatorOnDiscChange = val != "0";
                    break;
                case "mtap1":
                    if (val == "always") hasMtap1 = true;
                    break;
                case "mtap2":
                    if (val == "always") hasMtap2 = true;
                    break;
                case "lopnor-config":
                    opts.EnableLopnorConfig = val == "1";
                    break;
                case "path-trophydata": opts.PathTrophyData = val; break;
                case "path-patches": opts.PathPatches = val; break;
                case "path-emulog": opts.PathEmulog = val; break;
                case "path-featuredata": opts.PathFeatureData = val; break;
                case "host-osd": _ = int.TryParse(val, out int ho) ? opts.HostOsd = ho : 0; break;
                case "ps2-lang": opts.Ps2Lang = val; break;

                // Speed
                case "vu0-opt-flags": if (val == "1") opts.ImproveSpeed = true; break;

                // Graphics fixes
                case "fpu-no-clamping": if (val == "0") opts.FixGraphics = true; break;

                // MTVU
                case "vu1":
                    if (val == "jit-sync") opts.DisableMTVU = true;
                    break;

                // VIF1
                case "vif1-instant-xfer":
                    if (val == "0") opts.DisableInstantVIF1Transfer = true;
                    break;
            }
        }

        // Resolve multitap mode
        if (hasMtap1 && hasMtap2) opts.MultitapMode = MultitapMode.Both;
        else if (hasMtap1) opts.MultitapMode = MultitapMode.Port1;
        else if (hasMtap2) opts.MultitapMode = MultitapMode.Port2;

        return opts;
    }
}
