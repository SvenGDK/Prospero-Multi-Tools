// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Browser;
using ProsperoMultiTools.Settings;
using ProsperoMultiTools.Shell;
using ProsperoMultiTools.Tools.PS1Tools;
using ProsperoMultiTools.Tools.PS2Tools;
using ProsperoMultiTools.Tools.PS3Tools;
using ProsperoMultiTools.Tools.PS4Tools;
using ProsperoMultiTools.Tools.PS5Tools;
using ProsperoMultiTools.Tools.PSPTools;
using ProsperoMultiTools.Tools.PSVTools;
using SharpProspero.Application;
using SharpProspero.Platform;
using SharpProspero.Ui;
using System.Collections.Generic;
using System.Reflection;

namespace ProsperoMultiTools;

internal sealed class HomeScreen : MultiToolsScreen
{
    public HomeScreen(MultiToolsShell shell) : base(shell) { }

    public override string Title => "Multi Tools";

    public override string Hint => "Cross opens, Circle exits.";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 620 };

        Heading(menu, "Backup Managers");
        menu.Add(new Button("PS1 Backups", () => Shell.Push(new BackupBrowserScreen(Shell, Data.GamePlatform.PS1))));
        menu.Add(new Button("PS2 Backups", () => Shell.Push(new BackupBrowserScreen(Shell, Data.GamePlatform.PS2))));
        menu.Add(new Button("PS3 Backups", () => Shell.Push(new BackupBrowserScreen(Shell, Data.GamePlatform.PS3))));
        menu.Add(new Button("PS4 Backups", () => Shell.Push(new BackupBrowserScreen(Shell, Data.GamePlatform.PS4))));
        menu.Add(new Button("PS5 Backups", () => Shell.Push(new BackupBrowserScreen(Shell, Data.GamePlatform.PS5))));
        menu.Add(new Button("PSP Backups", () => Shell.Push(new BackupBrowserScreen(Shell, Data.GamePlatform.PSP))));
        menu.Add(new Button("PS Vita Backups", () => Shell.Push(new BackupBrowserScreen(Shell, Data.GamePlatform.PSVita))));

        Heading(menu, "PS1 / PS2 Tools");
        menu.Add(new Button("Merge BIN files (PS1)", () => Shell.Push(new BinMergeScreen(Shell, Data.GamePlatform.PS1))));
        menu.Add(new Button("Merge BIN files (PS2)", () => Shell.Push(new BinMergeScreen(Shell, Data.GamePlatform.PS2))));
        menu.Add(new Button("Convert BIN/CUE to ISO (PS2)", () => Shell.Push(new BinToIsoScreen(Shell))));

        Heading(menu, "PS5 Tools");
        menu.Add(new Button("Payload sender", () => Shell.Push(new PayloadSenderScreen(Shell))));
        menu.Add(new Button("Clear error history", () => Shell.Push(new ErrorHistoryScreen(Shell))));
        menu.Add(new Button("Read disc info", () => Shell.Push(new DiscInfoScreen(Shell))));
        menu.Add(new Button("Port checker", () => Shell.Push(new PortCheckerScreen(Shell))));

        Heading(menu, "Console Utilities");
        menu.Add(new Button("PS3 utilities", () => Shell.Push(new PS3ToolsScreen(Shell))));
        menu.Add(new Button("PS4 utilities", () => Shell.Push(new PS4ToolsScreen(Shell))));
        menu.Add(new Button("PSP utilities", () => Shell.Push(new PSPToolsScreen(Shell))));
        menu.Add(new Button("PS Vita utilities", () => Shell.Push(new PSVToolsScreen(Shell))));

        Heading(menu, "Shared Tools");
        menu.Add(new Button("Cross-platform tools", () => Shell.Push(new CrossPlatformScreen(Shell))));

        Heading(menu, "System");
        menu.Add(new Button("Settings", () => Shell.Push(new SettingsScreen(Shell))));
        menu.Add(new Button("Diagnostics", () => Shell.Push(new DiagnosticsScreen(Shell))));
        menu.Add(new Button("Refresh storage", () =>
        {
            Places.Refresh();
            RebuildScreen();
            int reachable = Places.Reachable.Count;
            Shell.Notify(reachable == 1
                ? "1 storage location available."
                : $"{reachable} storage locations available.");
        }));
        menu.Add(new Button("Unlock full storage access", () =>
        {
            UnjailResult unjail = UnjailRequest.Request();
            Places.Configure(unjail);
            Places.Refresh();
            RebuildScreen();
            int reachable = Places.Reachable.Count;
            if (unjail.Applied)
                Shell.Notify(reachable == 1
                    ? "Full storage access enabled. 1 location available."
                    : $"Full storage access enabled. {reachable} locations available.");
            else
                Shell.Notify("Storage access could not be expanded.");
        }));

        menu.Add(new Separator());
        menu.Add(new Button("Exit", Shell.RequestExit));

        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        int reachable = Places.Reachable.Count;

        var header = new StackPanel();
        header.Add(new Label("Prospero Multi Tools") { TextColor = Shell.Theme.Text });
        header.Add(Muted($"Backup toolkit for PS1 through PS5, PSP and PS Vita - v{version}"));
        header.Add(Muted($"{reachable} storage location{(reachable == 1 ? "" : "s")} available."));

        return new StackPanel()
            .Add(header)
            .Add(new Separator())
            .Add(menu);
    }

    private void Heading(ScrollMenu menu, string text)
    {
        menu.Add(new Separator());
        menu.Add(new Label(text) { TextColor = Shell.Theme.Accent });
    }

    private Label Muted(string text) => new(text) { TextColor = Shell.Theme.TextMuted };

    protected override bool OnCancel() => false;
}
