// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Data;
using ProsperoMultiTools.Shell;
using SharpProspero.Storage;
using SharpProspero.Ui;
using System;

namespace ProsperoMultiTools.Browser;

internal sealed class ParamEditScreen : MultiToolsScreen
{
    private readonly BackupInfo _backup;
    private readonly ParamJson _param;

    public ParamEditScreen(MultiToolsShell shell, BackupInfo backup) : base(shell)
    {
        _backup = backup;
        if (backup.Param is not null)
        {
            _param = backup.Param;
        }
        else
        {
            _param = new ParamJson();
            _backup.Param = _param;
        }
    }

    public override string Title => $"Edit param.json - {_backup.DisplayTitle}";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 600 };

        menu.Add(new Label("General") { TextColor = Shell.Theme.Accent });
        menu.Add(new Separator());

        AddEditableField(menu, "Title ID", _param.TitleId, v => _param.TitleId = v);
        AddEditableField(menu, "Content ID", _param.ContentId, v => _param.ContentId = v);
        AddEditableField(menu, "Content Version", _param.ContentVersion, v => _param.ContentVersion = v);
        AddEditableField(menu, "Master Version", _param.MasterVersion, v => _param.MasterVersion = v);
        AddEditableField(menu, "DRM Type", _param.ApplicationDrmType, v => _param.ApplicationDrmType = v);
        AddEditableField(menu, "Version File URI", _param.VersionFileUri, v => _param.VersionFileUri = v);

        menu.Add(new Separator());
        menu.Add(new Label("Localized Titles") { TextColor = Shell.Theme.Accent });
        menu.Add(new Separator());

        AddEditableField(menu, "Default Language", _param.DefaultLanguage, v => _param.DefaultLanguage = v);

        for (int i = 0; i < _param.LocalizedTitles.Count; i++)
        {
            LocalizedTitle lt = _param.LocalizedTitles[i];
            menu.Add(new Button($"{lt.Language}: {lt.TitleName}", () =>
            {
                Shell.Dialogs.AskText($"Title ({lt.Language})", lt.TitleName, newTitle =>
                {
                    if (newTitle is not null)
                    {
                        lt.TitleName = newTitle;
                        OnEdited($"Title for {lt.Language}");
                    }
                });
            }));
        }

        menu.Add(new Button("Add localized title", () =>
        {
            Shell.Dialogs.AskText("Language code (e.g. en-US)", "", lang =>
            {
                if (lang is null || lang.Length == 0)
                    return;
                foreach (LocalizedTitle existing in _param.LocalizedTitles)
                {
                    if (existing.Language == lang)
                    {
                        Shell.Notify($"Language '{lang}' already exists. Edit the existing entry instead.");
                        return;
                    }
                }
                Shell.Dialogs.AskText("Title name", "", title =>
                {
                    if (title is not null)
                    {
                        _param.LocalizedTitles.Add(new LocalizedTitle { Language = lang, TitleName = title });
                        OnEdited(lang);
                    }
                });
            });
        }));

        menu.Add(new Separator());
        menu.Add(new Label("Pubtools") { TextColor = Shell.Theme.Accent });
        menu.Add(new Separator());

        AddEditableField(menu, "Creation Date", _param.CreationDate, v => _param.CreationDate = v);
        AddEditableField(menu, "Tool Version", _param.ToolVersion, v => _param.ToolVersion = v);

        menu.Add(new Separator());
        menu.Add(new Button("Save param.json", () => SaveParam()));

        return menu;
    }

    private void OnEdited(string label)
    {
        RebuildScreen();
        Shell.Status($"{label} updated.");
    }

    private void AddEditableField(ScrollMenu menu, string label, string currentValue, Action<string> setter)
    {
        menu.Add(new Button($"{label}: {currentValue}", () =>
        {
            Shell.Dialogs.AskText($"Edit {label}", currentValue, newValue =>
            {
                if (newValue is not null)
                {
                    setter(newValue);
                    OnEdited(label);
                }
            });
        }));
    }

    private void SaveParam()
    {
        string paramPath = FindParamPath();
        if (string.IsNullOrEmpty(paramPath))
        {
            FilePickerScreen.PickFolder(Shell, "Pick a folder to save param.json into",
                Places.StartingPoint(Shell.Settings.StartPath) ?? "",
                path =>
                {
                    if (!string.IsNullOrEmpty(path))
                        WriteParam($"{path.TrimEnd('/')}/param.json");
                });
            return;
        }
        WriteParam(paramPath);
    }

    private void WriteParam(string path)
    {
        try
        {
            _param.WriteToFile(path);
            Shell.Notify($"param.json saved to {path}");
        }
        catch (Exception e)
        {
            Shell.ReportFailure("Save param.json", e, true);
        }
    }

    private string FindParamPath()
    {
        if (string.IsNullOrEmpty(_backup.FolderPath))
            return "";
        string path = $"{_backup.FolderPath}/sce_sys/param.json";
        if (FileSystem.Exists(path))
            return path;
        return "";
    }
}
