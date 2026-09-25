// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Shell;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;
using System.Text;

namespace ProsperoMultiTools.Emulator;

/// <summary>
/// Option model for the PS1 emulator (ps1hd).
/// Maps to <c>--key=value</c> flags in <c>config-title.txt</c>.
/// </summary>
internal sealed class Ps1EmuOptions : IEmuOptions
{
    // ---- Identity ----

    /// <summary>PS1 title identifier (e.g. "SLUS01222").</summary>
    public string Ps1TitleId { get; set; } = "";

    // ---- Disc images ----

    /// <summary>Disc image paths relative to the emulator root (e.g. "data/disc1.bin").</summary>
    public List<string> DiscPaths { get; } = [];

    // ---- BIOS / ROM ----

    /// <summary>BIOS ROM filename (empty = emulator default).</summary>
    public string RomBios { get; set; } = "";

    // ---- Graphics ----

    /// <summary>Rendering scale factor (1..8, where 6 is the ps1hd default).</summary>
    public int Scale { get; set; } = 6;

    /// <summary>Skip the BIOS boot logo screen.</summary>
    public bool BiosHideSceOsd { get; set; }

    /// <summary>Force 60Hz scanout (<c>--gpu-scanout-fps-override=60</c>).</summary>
    public bool Force60Hz { get; set; }

    /// <summary>Pace GPU DMA transfers (<c>--pace-gpu-dma=true</c>).</summary>
    public bool PaceGpuDma { get; set; }

    // ---- Controller ----

    /// <summary>Enable GunCon light-gun support (<c>--guncon</c>).</summary>
    public bool EnableGuncon { get; set; }

    /// <summary>
    /// Emulate analog sticks on a digital pad (<c>--sim-analog-pad=0x2020</c>).
    /// Zero means disabled; non-zero is the mapping bitmask.
    /// </summary>
    public int SimAnalogPad { get; set; }

    // ---- Copy protection ----

    /// <summary>LibCrypt protection sub-channel value (empty = no protection).</summary>
    public string LibCrypt { get; set; } = "";

    // ---- Trophy / feature flags ----

    /// <summary>PS4 trophy integration (0 = off).</summary>
    public int Ps4Trophies { get; set; }

    /// <summary>PS5 UDS integration (0 = off).</summary>
    public int Ps5Uds { get; set; }

    /// <summary>Trophy support (0 = off).</summary>
    public int Trophies { get; set; }

    // ---- Extra config files ----

    /// <summary>Path to a per-game TXT configuration file.</summary>
    public string TxtConfigPath { get; set; } = "";

    /// <summary>Path to a per-game LUA configuration script (copied into scripts/).</summary>
    public string LuaConfigPath { get; set; } = "";

    // ---- IEmuOptions ----

    void IEmuOptions.EmitConfig(StringBuilder builder) => builder.Append(ConfigFileEmitter.EmitPs1(this));

    void IEmuOptions.BuildOptionRows(ScrollMenu menu, MultiToolsShell shell, Action rebuild)
    {
        menu.Add(new Label("Graphics") { TextColor = shell.Theme.Accent });

        menu.Add(new Stepper("Render scale", Scale, 1, 8, changed: v =>
            Scale = (int)v));
        menu.Add(new Checkbox("Skip BIOS logo", BiosHideSceOsd, v =>
            BiosHideSceOsd = v));
        menu.Add(new Checkbox("Force 60Hz", Force60Hz, v =>
            Force60Hz = v));
        menu.Add(new Checkbox("Pace GPU DMA", PaceGpuDma, v =>
            PaceGpuDma = v));

        menu.Add(new Separator());
        menu.Add(new Label("Controller") { TextColor = shell.Theme.Accent });

        menu.Add(new Checkbox("GunCon support", EnableGuncon, v =>
            EnableGuncon = v));

        menu.Add(new Separator());
        menu.Add(new Label("Protection") { TextColor = shell.Theme.Accent });

        menu.Add(new Button($"LibCrypt: {(LibCrypt.Length > 0 ? LibCrypt : "(none)")}", () =>
            shell.Dialogs.AskText("LibCrypt value", LibCrypt, v =>
            {
                if (v is not null) { LibCrypt = v; rebuild(); }
            })));
    }
}
