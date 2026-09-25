// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Shell;
using SharpProspero.Ui;
using System;
using System.Text;

namespace ProsperoMultiTools.Emulator;

/// <summary>Anti-aliasing mode for the PSP emulator.</summary>
internal enum PspAntiAlias
{
    None,
    MSAA4x,
    SSAA4x,
}

/// <summary>
/// Option model for the PSP emulator (psphd).
/// Maps to <c>--key=value</c> flags in <c>config-title.txt</c>.
/// </summary>
internal sealed class PspEmuOptions : IEmuOptions
{
    // ---- Identity ----

    /// <summary>PSP disc serial (e.g. "UCUS-98501"). Empty leaves the header comment out.</summary>
    public string PspTitleId { get; set; } = "";

    // ---- Disc image ----

    /// <summary>UMD image path relative to the emulator root (e.g. "data/USER_L0.IMG").</summary>
    public string ImagePath { get; set; } = "data/USER_L0.IMG";

    // ---- Graphics ----

    /// <summary>Anti-aliasing mode.</summary>
    public PspAntiAlias AntiAlias { get; set; } = PspAntiAlias.SSAA4x;

    // ---- Save data ----

    /// <summary>Allow multiple save data slots.</summary>
    public bool MultiSaves { get; set; } = true;

    // ---- Trophy / feature flags ----

    /// <summary>PS4 trophy integration (0 = off).</summary>
    public int Ps4Trophies { get; set; }

    /// <summary>PS5 UDS integration (0 = off).</summary>
    public int Ps5Uds { get; set; }

    /// <summary>Trophy support (0 = off).</summary>
    public int Trophies { get; set; }

    /// <summary>Disable trophy tracking entirely.</summary>
    public bool NoTrophies { get; set; } = true;

    // ---- Extra config ----

    /// <summary>Path to a per-game TXT configuration file appended verbatim.</summary>
    public string TxtConfigPath { get; set; } = "";

    // ---- Helpers ----

    /// <summary>Returns the <c>--antialias</c> flag value for the current setting.</summary>
    internal string GetAntiAliasValue()
    {
        return AntiAlias switch
        {
            PspAntiAlias.None => "none",
            PspAntiAlias.MSAA4x => "MSAA4x",
            PspAntiAlias.SSAA4x => "SSAA4x",
            _ => "SSAA4x",
        };
    }

    // ---- IEmuOptions ----

    void IEmuOptions.EmitConfig(StringBuilder builder) => builder.Append(ConfigFileEmitter.EmitPsp(this));

    void IEmuOptions.BuildOptionRows(ScrollMenu menu, MultiToolsShell shell, Action rebuild)
    {
        menu.Add(new Label("Graphics") { TextColor = shell.Theme.Accent });

        string[] aaLabels = ["None", "MSAA 4x", "SSAA 4x"];
        menu.Add(new OptionSelector("Anti-aliasing", aaLabels, (int)AntiAlias, i =>
            AntiAlias = (PspAntiAlias)i));

        menu.Add(new Separator());
        menu.Add(new Label("Save data") { TextColor = shell.Theme.Accent });

        menu.Add(new Checkbox("Multiple save slots", MultiSaves, v =>
            MultiSaves = v));

        menu.Add(new Separator());
        menu.Add(new Label("Trophies") { TextColor = shell.Theme.Accent });

        menu.Add(new Checkbox("Disable trophy tracking", NoTrophies, v =>
            NoTrophies = v));
    }
}
