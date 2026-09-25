// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Shell;
using SharpProspero.Ui;
using System;

namespace ProsperoMultiTools.Settings;

internal sealed class SettingsScreen : MultiToolsScreen
{
    private readonly AppSettings _settings;

    public SettingsScreen(MultiToolsShell shell) : base(shell)
    {
        _settings = shell.Settings;
    }

    public override string Title => "Settings";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 600 };

        Section(menu, "Paths");

        menu.Add(new Button($"Start path: {(_settings.StartPath.Length > 0 ? _settings.StartPath : "(default)")}", () =>
        {
            FilePickerScreen.PickFolder(Shell, "Pick the start folder",
                _settings.StartPath, value =>
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        _settings.StartPath = value;
                        SaveAndRebuild();
                    }
                });
        }));

        menu.Add(new Button($"Backup search path: {(_settings.BackupSearchPath.Length > 0 ? _settings.BackupSearchPath : "(default: /data and /mnt/usb*)")}", () =>
        {
            FilePickerScreen.PickFolder(Shell, "Pick the folder backups are scanned from",
                _settings.BackupSearchPath, value =>
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        _settings.BackupSearchPath = value;
                        SaveAndRebuild();
                    }
                });
        }));

        if (_settings.BackupSearchPath.Length > 0)
        {
            menu.Add(new Button("Clear backup search path (use /data and /mnt/usb*)", () =>
            {
                _settings.BackupSearchPath = "";
                SaveAndRebuild();
            }));
        }

        Section(menu, "Display");

        menu.Add(new Stepper("Text scale", _settings.TextScale, 1, 4, changed: v =>
        {
            _settings.TextScale = (int)v;
            SavePersist();
        }));

        menu.Add(new Stepper("List rows", _settings.ListRows, 6, 30, changed: v =>
        {
            _settings.ListRows = (int)v;
            SavePersist();
        }));

        Section(menu, "Behavior");

        menu.Add(new Checkbox("Confirm before delete / move", _settings.ConfirmDestructive, v =>
        {
            _settings.ConfirmDestructive = v;
            SavePersist();
        }));

        menu.Add(new Checkbox("Show hidden files", _settings.ShowHiddenFiles, v =>
        {
            _settings.ShowHiddenFiles = v;
            SavePersist();
        }));

        menu.Add(new Checkbox("System notifications", _settings.SystemNotifications, v =>
        {
            _settings.SystemNotifications = v;
            SavePersist();
        }));

        menu.Add(new Slider("Notification time", 1f, 10f, _settings.ToastSeconds, 0.5f, v =>
        {
            _settings.ToastSeconds = v;
            SavePersist();
        }));

        Section(menu, "Audio");

        menu.Add(new Checkbox("Play soundtrack when available", _settings.PlaySoundtrack, v =>
        {
            _settings.PlaySoundtrack = v;
            SavePersist();
        }));

        menu.Add(new Stepper("Volume", _settings.Volume, 0, 100, 5, v => $"{v}%", v =>
        {
            _settings.Volume = (int)v;
            SavePersist();
        }));

        Section(menu, "Payload sender");

        menu.Add(new Button($"Host: {_settings.PayloadHost}", () =>
        {
            Shell.Dialogs.AskText("Payload host", _settings.PayloadHost, value =>
            {
                if (value is not null && value.Length > 0)
                {
                    _settings.PayloadHost = value;
                    SaveAndRebuild();
                }
            });
        }));

        menu.Add(new Stepper("Port", _settings.PayloadPort, 1, 65535, 1, null, v =>
        {
            _settings.PayloadPort = (int)v;
            SavePersist();
        }));

        Section(menu, "Reset");

        menu.Add(new Button("Reset all settings to defaults", () =>
        {
            Shell.Dialogs.Confirm("Reset all settings to their defaults?", yes =>
            {
                if (yes)
                {
                    _settings.Reset();
                    if (!_settings.Save())
                        Shell.Notify("Settings could not be saved.");
                    RebuildScreen();
                    Shell.Notify("Settings reset to defaults.");
                }
            });
        }));

        return menu;
    }

    private void Section(ScrollMenu menu, string text)
    {
        menu.Add(new Separator());
        menu.Add(new Label(text) { TextColor = Shell.Theme.Accent });
    }

    /// <summary>Persists without rebuilding. Stepper, Slider and Checkbox controls update in place.</summary>
    private void SavePersist()
    {
        if (_settings.Save())
            Shell.Status("Saved.");
        else
            Shell.Notify("Settings could not be saved.");
    }

    /// <summary>Persists and rebuilds. Button rows whose captions reflect the current value need this.</summary>
    private void SaveAndRebuild()
    {
        if (_settings.Save())
            Shell.Status("Saved.");
        else
            Shell.Notify("Settings could not be saved.");
        RebuildScreen();
    }
}
