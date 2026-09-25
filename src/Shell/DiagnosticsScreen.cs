// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Emulator;
using SharpProspero.Storage;
using SharpProspero.Ui;
using System.Reflection;

namespace ProsperoMultiTools.Shell;

/// <summary>
/// A read-only status screen that tells a user why the shell is behaving the way it is on their
/// console. Every line is a snapshot fact from the last refresh: whether the escalation daemon
/// answered, which candidate paths the probe answered for, whether the emulator folders resolved
/// on any of the probe paths, and the module version. A user who reports a persistent bug ("I
/// still cannot read /data") can hand a screenshot of this page to whoever is looking into it
/// and it reads the same on every console.
/// </summary>
internal sealed class DiagnosticsScreen : MultiToolsScreen
{
    public DiagnosticsScreen(MultiToolsShell shell) : base(shell) { }

    public override string Title => "Diagnostics";

    public override string Hint => "Cross refreshes, L1/R1 page, L2/R2 jump, Circle goes back.";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 620 };
        AppendSnapshotRows(menu);
        menu.Add(new Separator());
        menu.Add(new Button("Refresh", () =>
        {
            // A rebuild re-runs BuildRoot which reads every diagnostic fact fresh, so the snapshot
            // reflects the current state after a user has fixed something (attached a USB, sent
            // the escalation daemon, deployed an emulator) without leaving the page.
            RebuildScreen();
        }));
        return menu;
    }

    // Emits one row per fact, so a snapshot with sixty facts draws as sixty rows the scroll view
    // pages through, not one truncated line the Label control clips to its width.
    private void AppendSnapshotRows(ScrollMenu menu)
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        menu.Add(Head("Module version: " + version));

        if (Places.IsUnjailApplied)
        {
            menu.Add(Ok("Escalation daemon: applied."));
            menu.Add(Muted("The file view has been widened for this process."));
        }
        else
        {
            menu.Add(Warn("Escalation daemon: not applied."));
            string reason = string.IsNullOrEmpty(Places.UnjailReason)
                ? "The daemon was not reachable."
                : Places.UnjailReason;
            menu.Add(Muted(reason));
        }

        menu.Add(new Separator());
        menu.Add(Head("Reachable folders (" + Places.Reachable.Count + ")"));
        if (Places.Reachable.Count == 0)
            menu.Add(Muted("(none)"));
        else
        {
            foreach (PlaceProbe probe in Places.Reachable)
            {
                string label = probe.ViaBroker
                    ? probe.Place.Path + "    (via broker)"
                    : probe.Place.Path;
                menu.Add(Muted(label));
            }
        }

        if (Places.Unreachable.Count > 0)
        {
            menu.Add(new Separator());
            menu.Add(Head("Unreachable folders (" + Places.Unreachable.Count + ")"));
            foreach (PlaceProbe probe in Places.Unreachable)
            {
                menu.Add(Muted(probe.Place.Path + "    -    " + probe.Reason));
            }
        }

        // Show the broker's own reachability alongside the folders so a snapshot pins down
        // whether /data and /user are being served through the daemon (a "via broker" row above)
        // rather than through the direct file view.
        menu.Add(new Separator());
        menu.Add(Head("File broker"));
        if (SandboxBroker.IsReachable())
            menu.Add(Ok("Reachable on loopback port. /data and /user route through it."));
        else
            menu.Add(Warn("Not reachable. /data and /user will not answer for this session."));

        menu.Add(new Separator());
        menu.Add(Head("Emulators"));
        var descriptors = EmulatorRegistry.All;
        if (descriptors.Count == 0)
            menu.Add(Muted("(no emulator entries in the manifest)"));
        else
        {
            foreach (EmulatorDescriptor emu in descriptors)
            {
                string? folder = EmulatorRegistry.ResolveFolder(emu);
                if (folder is null)
                {
                    menu.Add(Muted(emu.Name + "  (" + emu.Kind + ")  -  not installed"));
                }
                else
                {
                    menu.Add(Muted(emu.Name + "  (" + emu.Kind + ")  -  at " + folder));
                    string? realTitleId = EmulatorRegistry.ReadTitleIdFromFolder(folder);
                    if (!string.IsNullOrEmpty(realTitleId))
                        menu.Add(Muted("    title id " + realTitleId));
                }
            }
        }
    }

    private Label Head(string text) => new(text) { TextColor = Shell.Theme.Accent };
    private Label Muted(string text) => new(text) { TextColor = Shell.Theme.TextMuted };
    private Label Ok(string text) => new(text) { TextColor = Shell.Theme.Text };
    private Label Warn(string text) => new(text) { TextColor = Shell.Theme.Accent };
}
