// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Data;
using ProsperoMultiTools.Shell;
using SharpProspero.Storage;
using SharpProspero.Storage.Sfo;
using SharpProspero.Ui;
using System;

namespace ProsperoMultiTools.Browser;

internal sealed class SfoEditScreen : MultiToolsScreen
{
    private readonly BackupInfo _backup;
    private readonly SfoFile _sfo;

    public SfoEditScreen(MultiToolsShell shell, BackupInfo backup) : base(shell)
    {
        _backup = backup;
        _sfo = backup.Sfo ?? new SfoFile();
    }

    public override string Title => $"Edit SFO - {_backup.DisplayTitle}";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 600 };

        menu.Add(new Label("SFO Entries") { TextColor = Shell.Theme.Accent });
        menu.Add(new Separator());

        for (int i = 0; i < _sfo.Entries.Count; i++)
        {
            SfoEntry entry = _sfo.Entries[i];
            int idx = i;

            if (entry.ValueType == SfoValueType.String)
            {
                menu.Add(new Button($"{entry.Key} = {entry.StringValue}", () =>
                {
                    Shell.Dialogs.AskText($"Edit {entry.Key}", entry.StringValue, newValue =>
                    {
                        if (newValue is not null)
                        {
                            _sfo.SetString(entry.Key, newValue);
                            RebuildScreen();
                            Shell.Status($"{entry.Key} updated.");
                        }
                    });
                }));
            }
            else if (entry.ValueType == SfoValueType.Int32)
            {
                menu.Add(new Button($"{entry.Key} = {entry.Int32Value}", () =>
                {
                    Shell.Dialogs.AskText($"Edit {entry.Key}", entry.Int32Value.ToString(), newValue =>
                    {
                        if (newValue is null)
                            return;
                        if (!int.TryParse(newValue, out int parsed))
                        {
                            Shell.Notify($"'{newValue}' is not a valid integer.");
                            return;
                        }
                        entry.Int32Value = parsed;
                        RebuildScreen();
                        Shell.Status($"{entry.Key} updated.");
                    });
                }));
            }
            else
            {
                menu.Add(new KeyValueRow(entry.Key, $"[{entry.RawValue.Length} bytes]"));
            }
        }

        menu.Add(new Separator());
        menu.Add(new Button("Add string entry", () =>
        {
            Shell.Dialogs.AskText("Key name", "", key =>
            {
                if (key is null || key.Length == 0)
                    return;
                Shell.Dialogs.AskText("Value", "", value =>
                {
                    if (value is not null)
                    {
                        _sfo.SetString(key, value);
                        RebuildScreen();
                        Shell.Status($"Added {key}.");
                    }
                });
            });
        }));
        menu.Add(new Button("Add integer entry", () =>
        {
            Shell.Dialogs.AskText("Key name", "", key =>
            {
                if (key is null || key.Length == 0)
                    return;
                Shell.Dialogs.AskText("Value", "0", value =>
                {
                    if (value is not null && int.TryParse(value, out int parsed))
                    {
                        _sfo.SetInt32(key, parsed);
                        RebuildScreen();
                        Shell.Status($"Added {key}.");
                    }
                });
            });
        }));

        menu.Add(new Separator());
        menu.Add(new Button("Save SFO", () => SaveSfo()));

        return menu;
    }

    private void SaveSfo()
    {
        string sfoPath = FindSfoPath();
        if (string.IsNullOrEmpty(sfoPath))
        {
            FilePickerScreen.PickFolder(Shell, "Pick a folder to save param.sfo into",
                Places.StartingPoint(Shell.Settings.StartPath) ?? "",
                path =>
                {
                    if (!string.IsNullOrEmpty(path))
                        WriteSfo($"{path.TrimEnd('/')}/param.sfo");
                });
            return;
        }
        WriteSfo(sfoPath);
    }

    private void WriteSfo(string path)
    {
        try
        {
            _sfo.WriteToFile(path);
            Shell.Notify($"SFO saved to {path}");
        }
        catch (Exception e)
        {
            Shell.ReportFailure("Save SFO", e, true);
        }
    }

    private string FindSfoPath()
    {
        if (string.IsNullOrEmpty(_backup.FolderPath))
            return "";

        switch (_backup.Platform)
        {
            case GamePlatform.PS3:
                string ps3Path = $"{_backup.FolderPath}/PS3_GAME/PARAM.SFO";
                if (FileSystem.Exists(ps3Path)) return ps3Path;
                string ps3Alt = $"{_backup.FolderPath}/PARAM.SFO";
                if (FileSystem.Exists(ps3Alt)) return ps3Alt;
                break;
            case GamePlatform.PS4:
            case GamePlatform.PSVita:
                string ps4Path = $"{_backup.FolderPath}/sce_sys/param.sfo";
                if (FileSystem.Exists(ps4Path)) return ps4Path;
                break;
            case GamePlatform.PS5:
                // PS5 uses sce_sys/param.json, not param.sfo. SFO editing does not apply.
                break;
            case GamePlatform.PSP:
                string pspPath = $"{_backup.FolderPath}/PSP_GAME/PARAM.SFO";
                if (FileSystem.Exists(pspPath)) return pspPath;
                break;
        }
        return "";
    }
}
