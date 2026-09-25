// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Data;
using ProsperoMultiTools.Shell;
using SharpProspero.Storage;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;

namespace ProsperoMultiTools.Tools.PS2Tools;

internal sealed class BinToIsoScreen : MultiToolsScreen
{
    private const int CdSectorSize = 2352;
    private const int IsoSectorSize = 2048;
    private const int Mode1HeaderSize = 16;
    private const int Mode2Form1HeaderSize = 24;

    private string _binPath = "";
    private string _cuePath = "";
    private string _outputPath = "";
    private CueSheet? _cue;
    private string _trackMode = "";

    public BinToIsoScreen(MultiToolsShell shell) : base(shell) { }

    public override string Title => "Convert BIN/CUE to ISO (PS2)";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 500 };

        menu.Add(new Label("Convert a BIN/CUE disc image to ISO format.") { TextColor = Shell.Theme.TextMuted });
        menu.Add(new Separator());

        menu.Add(new Button($"CUE file: {(_cuePath.Length > 0 ? _cuePath : "(not selected)")}", () =>
        {
            FilePickerScreen.PickFile(Shell, "Pick a .cue file",
                _cuePath.Length > 0 ? _cuePath : (Places.StartingPoint(Shell.Settings.StartPath) ?? ""),
                [".cue"],
                path => { if (!string.IsNullOrEmpty(path)) LoadCue(path); });
        }));

        if (!string.IsNullOrEmpty(_binPath))
            menu.Add(new KeyValueRow("BIN file", _binPath));
        if (!string.IsNullOrEmpty(_trackMode))
            menu.Add(new KeyValueRow("Track mode", _trackMode));

        menu.Add(new Separator());
        menu.Add(new Button($"Output folder: {(_outputPath.Length > 0 ? _outputPath : "(auto)")}", () =>
        {
            FilePickerScreen.PickFolder(Shell, "Pick an output folder",
                Places.StartingPoint(Shell.Settings.StartPath) ?? "",
                path =>
                {
                    if (string.IsNullOrEmpty(path))
                        return;
                    string baseName = string.IsNullOrEmpty(_binPath) ? "converted" : TrimExtension(GetFileName(_binPath));
                    _outputPath = $"{path.TrimEnd('/')}/{baseName}.iso";
                    RebuildScreen();
                });
        }));

        menu.Add(new Separator());
        menu.Add(new Button("Convert", () =>
        {
            if (string.IsNullOrEmpty(_binPath))
            {
                Shell.Notify("Select a CUE file first.");
                return;
            }
            ConvertToIso();
        }));

        return menu;
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

    private void LoadCue(string path)
    {
        _cuePath = path;
        _cue = CueSheet.ParseFromFile(path);

        if (_cue is null || _cue.Files.Count == 0)
        {
            Shell.Notify("Could not parse the CUE file.");
            _binPath = "";
            _trackMode = "";
            RebuildScreen();
            return;
        }

        var binPaths = _cue.GetBinFilePathsResolved(path, FileSystem.Exists);
        _binPath = binPaths.Count > 0 ? binPaths[0] : "";

        if (_cue.Files[0].Tracks.Count > 0)
            _trackMode = _cue.Files[0].Tracks[0].Type;

        if (_outputPath.Length == 0 && _binPath.Length > 0)
        {
            int dot = _binPath.LastIndexOf('.');
            _outputPath = (dot > 0 ? _binPath.Substring(0, dot) : _binPath) + ".iso";
        }

        RebuildScreen();
    }

    private void ConvertToIso()
    {
        if (!FileSystem.Exists(_binPath))
        {
            Shell.Notify($"BIN file not found: {_binPath}");
            return;
        }

        if (string.Equals(_binPath, _outputPath, StringComparison.OrdinalIgnoreCase))
        {
            Shell.Notify("Output path must differ from input BIN file.");
            return;
        }

        var segments = BuildConvertSegments();
        if (segments is null)
            return;

        long totalSectors = 0;
        foreach (var seg in segments)
            totalSectors += seg.SectorCount;

        if (totalSectors == 0)
        {
            Shell.Notify("No data sectors to convert.");
            return;
        }

        int maxRawSectorSize = 0;
        foreach (var seg in segments)
        {
            if (seg.RawSectorSize > maxRawSectorSize)
                maxRawSectorSize = seg.RawSectorSize;
        }

        if (maxRawSectorSize > 2500)
        {
            Shell.Notify($"Unsupported sector size {maxRawSectorSize} (maximum is 2500).");
            return;
        }

        long sectorsProcessed = 0;
        int segIndex = 0;
        DeviceFileStream? input = null;
        DeviceFileStream? output = null;
        byte[] sectorBuffer = new byte[maxRawSectorSize];

        Shell.Dialogs.RunWithProgress("Converting to ISO...", dialog =>
        {
            try
            {
                if (input is null)
                {
                    input = FileSystem.OpenRead(_binPath);
                    output = FileSystem.Create(_outputPath);
                }

                int batchSize = 64;

                for (int b = 0; b < batchSize && segIndex < segments.Count; b++)
                {
                    var seg = segments[segIndex];
                    int read = input!.Read(sectorBuffer, 0, seg.RawSectorSize);
                    if (read < seg.RawSectorSize)
                    {
                        segIndex = segments.Count;
                        break;
                    }

                    output!.Write(sectorBuffer, seg.HeaderSkip, seg.DataSize);
                    sectorsProcessed++;

                    seg.SectorsRemaining--;
                    if (seg.SectorsRemaining <= 0)
                        segIndex++;
                }

                if (segIndex >= segments.Count)
                {
                    input!.Dispose();
                    output!.Dispose();
                    return true;
                }

                return false;
            }
            catch (Exception e)
            {
                input?.Dispose();
                output?.Dispose();
                Shell.ReportFailure("Convert", e, true);
                return true;
            }
        }, () =>
        {
            long outSize = FileSystem.Exists(_outputPath) ? FileSystem.GetFileSize(_outputPath) : 0;
            Shell.Notify($"Converted {sectorsProcessed} sectors to {_outputPath} ({Formatting.FormatSize(outSize)})");
        });
    }

    private List<ConvertSegment>? BuildConvertSegments()
    {
        long binSize = FileSystem.GetFileSize(_binPath);
        var segments = new List<ConvertSegment>();

        if (_cue is null || _cue.Files.Count == 0 || _cue.Files[0].Tracks.Count == 0)
        {
            long count = binSize / CdSectorSize;
            if (count > 0)
            {
                segments.Add(new ConvertSegment
                {
                    RawSectorSize = CdSectorSize,
                    HeaderSkip = Mode1HeaderSize,
                    DataSize = IsoSectorSize,
                    SectorCount = count,
                    SectorsRemaining = count
                });
            }
            return segments;
        }

        CueFile file = _cue.Files[0];
        var trackInfos = new List<(int RawSize, int HeaderSkip, int DataSize, long StartSector)>();

        foreach (CueTrack track in file.Tracks)
        {
            if (track.Type.StartsWith("AUDIO", StringComparison.OrdinalIgnoreCase))
            {
                Shell.Notify("Cannot convert discs with AUDIO tracks to ISO.");
                return null;
            }

            ParseTrackMode(track.Type, out int rawSize, out int headerSkip, out int dataSize);
            long startSector = 0;
            if (!string.IsNullOrEmpty(track.Index01))
                startSector = CueSheet.TimestampToSectors(track.Index01);

            trackInfos.Add((rawSize, headerSkip, dataSize, startSector));
        }

        long bytesConsumed = 0;

        for (int i = 0; i < trackInfos.Count; i++)
        {
            var info = trackInfos[i];
            long sectorCount;

            if (i + 1 < trackInfos.Count)
            {
                sectorCount = trackInfos[i + 1].StartSector - info.StartSector;
            }
            else
            {
                long remainingBytes = binSize - bytesConsumed;
                sectorCount = remainingBytes / info.RawSize;
            }

            if (sectorCount <= 0)
                continue;

            bytesConsumed += sectorCount * info.RawSize;

            segments.Add(new ConvertSegment
            {
                RawSectorSize = info.RawSize,
                HeaderSkip = info.HeaderSkip,
                DataSize = info.DataSize,
                SectorCount = sectorCount,
                SectorsRemaining = sectorCount
            });
        }

        return segments;
    }

    private static void ParseTrackMode(string mode, out int rawSectorSize, out int headerSkip, out int dataSize)
    {
        dataSize = IsoSectorSize;

        if (string.IsNullOrEmpty(mode))
        {
            rawSectorSize = CdSectorSize;
            headerSkip = Mode1HeaderSize;
            return;
        }

        string upper = mode.ToUpperInvariant();

        int slash = upper.IndexOf('/');
        if (slash >= 0 && int.TryParse(upper.Substring(slash + 1), out int parsed))
            rawSectorSize = parsed;
        else
            rawSectorSize = CdSectorSize;

        if (rawSectorSize == IsoSectorSize)
        {
            headerSkip = 0;
            return;
        }

        headerSkip = upper.StartsWith("MODE2") ? Mode2Form1HeaderSize : Mode1HeaderSize;
    }

    private sealed class ConvertSegment
    {
        public int RawSectorSize;
        public int HeaderSkip;
        public int DataSize;
        public long SectorCount;
        public long SectorsRemaining;
    }
}
