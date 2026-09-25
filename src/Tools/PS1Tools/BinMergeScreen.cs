// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Data;
using ProsperoMultiTools.Shell;
using SharpProspero.Storage;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;

namespace ProsperoMultiTools.Tools.PS1Tools;

internal sealed class BinMergeScreen : MultiToolsScreen
{
    private readonly GamePlatform _platform;
    private string _cuePath = "";
    private string _outputPath = "";
    private CueSheet? _cue;
    private List<string> _binFiles = [];

    public BinMergeScreen(MultiToolsShell shell, GamePlatform platform) : base(shell)
    {
        _platform = platform;
    }

    public override string Title => $"Merge BIN Files ({(_platform == GamePlatform.PS1 ? "PS1" : "PS2")})";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 500 };

        menu.Add(new Label("Merge multiple .bin files from a BIN/CUE set into a single .bin and .cue") { TextColor = Shell.Theme.TextMuted });
        menu.Add(new Separator());

        menu.Add(new Button($"CUE file: {(_cuePath.Length > 0 ? _cuePath : "(not selected)")}", () =>
        {
            FilePickerScreen.PickFile(Shell, "Pick a .cue file",
                _cuePath.Length > 0 ? _cuePath : (Places.StartingPoint(Shell.Settings.StartPath) ?? ""),
                [".cue"],
                path => { if (!string.IsNullOrEmpty(path)) LoadCue(path); });
        }));

        if (_cue is not null && _binFiles.Count > 0)
        {
            menu.Add(new Separator());
            menu.Add(new Label($"{_binFiles.Count} BIN files found:") { TextColor = Shell.Theme.Accent });
            foreach (string bin in _binFiles)
                menu.Add(new Label($"  {bin}") { TextColor = Shell.Theme.TextMuted });
        }

        menu.Add(new Separator());
        menu.Add(new Button($"Output folder: {(_outputPath.Length > 0 ? _outputPath : "(auto)")}", () =>
        {
            FilePickerScreen.PickFolder(Shell, "Pick an output folder",
                Places.StartingPoint(Shell.Settings.StartPath) ?? "",
                path =>
                {
                    if (string.IsNullOrEmpty(path))
                        return;
                    string baseName = _cue is null ? "merged" : TrimExtension(GetFileName(_cuePath));
                    _outputPath = $"{path.TrimEnd('/')}/{baseName}_merged";
                    RebuildScreen();
                });
        }));

        menu.Add(new Separator());
        menu.Add(new Button("Merge", () =>
        {
            if (_cue is null || _binFiles.Count == 0)
            {
                Shell.Notify("Select a valid CUE file first.");
                return;
            }
            MergeBins();
        }));

        return menu;
    }

    private void LoadCue(string path)
    {
        _cuePath = path;
        _cue = CueSheet.ParseFromFile(path);
        _binFiles.Clear();

        if (_cue is null)
        {
            Shell.Notify("Could not parse the CUE file.");
            RebuildScreen();
            return;
        }

        _binFiles = _cue.GetBinFilePathsResolved(path, FileSystem.Exists);

        if (_outputPath.Length == 0)
        {
            string dir = _cue.GetDirectory(path);
            string fileName = GetFileName(path);
            int dot = fileName.LastIndexOf('.');
            string baseName = dot > 0 ? fileName.Substring(0, dot) : fileName;
            _outputPath = string.IsNullOrEmpty(dir) ? $"{baseName}_merged" : $"{dir}/{baseName}_merged";
        }

        RebuildScreen();
        Shell.Status($"{_binFiles.Count} BIN files found.");
    }

    private void MergeBins()
    {
        string outBin = $"{_outputPath}.bin";
        string outCue = $"{_outputPath}.cue";

        foreach (string bin in _binFiles)
        {
            if (!FileSystem.Exists(bin))
            {
                Shell.Notify($"Missing: {bin}");
                return;
            }
        }

        int fileIndex = 0;
        long totalWritten = 0;
        DeviceFileStream? output = null;
        DeviceFileStream? currentInput = null;
        byte[] buffer = new byte[1024 * 1024];

        Shell.Dialogs.RunWithProgress("Merging BIN files...", dialog =>
        {
            try
            {
                if (output is null)
                    output = FileSystem.Create(outBin);

                if (currentInput is null)
                {
                    if (fileIndex >= _binFiles.Count)
                    {
                        output.Dispose();
                        output = null;
                        WriteMergedCue(outCue, outBin, totalWritten);
                        return true;
                    }
                    currentInput = FileSystem.OpenRead(_binFiles[fileIndex]);
                }

                int bytesRead = currentInput.Read(buffer, 0, buffer.Length);
                if (bytesRead > 0)
                {
                    output.Write(buffer, 0, bytesRead);
                    totalWritten += bytesRead;
                }

                if (bytesRead < buffer.Length)
                {
                    currentInput.Dispose();
                    currentInput = null;
                    fileIndex++;
                }

                return false;
            }
            catch (Exception e)
            {
                currentInput?.Dispose();
                output?.Dispose();
                Shell.ReportFailure("Merge", e, true);
                return true;
            }
        }, () => Shell.Notify($"Merged {fileIndex} files into {outBin} ({Formatting.FormatSize(totalWritten)})"));
    }

    private void WriteMergedCue(string cuePath, string binPath, long binSize)
    {
        string binName = GetFileName(binPath);

        var sb = new System.Text.StringBuilder();
        sb.Append("FILE \"");
        sb.Append(binName);
        sb.Append("\" BINARY\r\n");

        if (_cue is not null)
        {
            long fileBaseOffset = 0;
            int trackNum = 1;

            foreach (CueFile file in _cue.Files)
            {
                int sectorSize = 2352;

                foreach (CueTrack track in file.Tracks)
                {
                    sb.Append("  TRACK ");
                    sb.Append(trackNum.ToString("D2"));
                    sb.Append(' ');
                    sb.Append(track.Type.Length > 0 ? track.Type : "MODE2/2352");
                    sb.Append("\r\n");

                    if (!string.IsNullOrEmpty(track.PreGap))
                    {
                        sb.Append("    PREGAP ");
                        sb.Append(track.PreGap);
                        sb.Append("\r\n");
                    }

                    long baseSectors = fileBaseOffset / sectorSize;

                    if (!string.IsNullOrEmpty(track.Index00))
                    {
                        long localIndex00 = CueSheet.TimestampToSectors(track.Index00);
                        sb.Append("    INDEX 00 ");
                        sb.Append(SectorsToTimestamp(baseSectors + localIndex00));
                        sb.Append("\r\n");
                    }

                    long localIndex01 = 0;
                    if (!string.IsNullOrEmpty(track.Index01))
                        localIndex01 = CueSheet.TimestampToSectors(track.Index01);

                    sb.Append("    INDEX 01 ");
                    sb.Append(SectorsToTimestamp(baseSectors + localIndex01));
                    sb.Append("\r\n");

                    trackNum++;
                }

                string fileBinPath = file.FileName;
                string dir = _cue.GetDirectory(_cuePath);
                string fullBinPath = string.IsNullOrEmpty(dir) ? fileBinPath : $"{dir}/{fileBinPath}";
                if (FileSystem.Exists(fullBinPath))
                    fileBaseOffset += FileSystem.GetFileSize(fullBinPath);
            }
        }

        FileSystem.WriteAllText(cuePath, sb.ToString());
    }

    private static string SectorsToTimestamp(long sectors)
    {
        int minutes = (int)(sectors / (75 * 60));
        int seconds = (int)((sectors / 75) % 60);
        int frames = (int)(sectors % 75);
        return $"{minutes:D2}:{seconds:D2}:{frames:D2}";
    }

    private static string GetFileName(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path.Substring(slash + 1) : path;
    }

    private static string TrimExtension(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name.Substring(0, dot) : name;
    }
}
