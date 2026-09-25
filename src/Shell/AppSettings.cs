// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using SharpProspero.Storage;
using System;
using System.Text;

namespace ProsperoMultiTools.Shell;

internal sealed class AppSettings
{
    private string? _filePath;

    public string StartPath { get; set; } = "";
    public bool ConfirmDestructive { get; set; } = true;
    public bool ShowHiddenFiles { get; set; }
    public bool SystemNotifications { get; set; } = true;
    public float ToastSeconds { get; set; } = 3f;
    public int TextScale { get; set; } = 2;
    public int ListRows { get; set; } = 14;
    public bool PlaySoundtrack { get; set; } = true;
    public int Volume { get; set; } = 80;
    public string PayloadHost { get; set; } = "localhost";
    public int PayloadPort { get; set; } = 9021;
    public string BackupSearchPath { get; set; } = "";

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        string? folder = Places.DataFolder("prospero-multi-tools");
        if (folder is null)
            return settings;

        settings._filePath = $"{folder}/settings.json";

        JsonValue json;
        try
        {
            json = LoadJson(settings._filePath);
        }
        catch (Exception)
        {
            return settings;
        }

        if (json.IsNull)
            return settings;

        settings.StartPath = json.GetString("startPath", settings.StartPath);
        settings.ConfirmDestructive = json.GetBool("confirmDestructive", settings.ConfirmDestructive);
        settings.ShowHiddenFiles = json.GetBool("showHiddenFiles", settings.ShowHiddenFiles);
        settings.SystemNotifications = json.GetBool("systemNotifications", settings.SystemNotifications);
        settings.ToastSeconds = Clamp((float)json.GetNumber("toastSeconds", settings.ToastSeconds), 1f, 10f);
        settings.TextScale = Clamp(json.GetInt("textScale", settings.TextScale), 1, 4);
        settings.ListRows = Clamp(json.GetInt("listRows", settings.ListRows), 6, 30);
        settings.PlaySoundtrack = json.GetBool("playSoundtrack", settings.PlaySoundtrack);
        settings.Volume = Clamp(json.GetInt("volume", settings.Volume), 0, 100);
        settings.PayloadHost = json.GetString("payloadHost", settings.PayloadHost);
        settings.PayloadPort = Clamp(json.GetInt("payloadPort", settings.PayloadPort), 1, 65535);
        settings.BackupSearchPath = json.GetString("backupSearchPath", settings.BackupSearchPath);

        return settings;
    }

    public bool Save()
    {
        if (_filePath is null)
            return false;

        try
        {
            var json = JsonValue.NewObject();
            json["startPath"] = StartPath;
            json["confirmDestructive"] = ConfirmDestructive;
            json["showHiddenFiles"] = ShowHiddenFiles;
            json["systemNotifications"] = SystemNotifications;
            json["toastSeconds"] = (double)ToastSeconds;
            json["textScale"] = TextScale;
            json["listRows"] = ListRows;
            json["playSoundtrack"] = PlaySoundtrack;
            json["volume"] = Volume;
            json["payloadHost"] = PayloadHost;
            json["payloadPort"] = PayloadPort;
            json["backupSearchPath"] = BackupSearchPath;
            return SaveJson(_filePath, json);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Reads a JSON blob. When the target path lives outside the module's own sandbox bind set
    // (paths under /data or /user that only the daemon's view reaches), the read is routed
    // through the file broker; otherwise it reaches the file system directly.
    private static JsonValue LoadJson(string path)
    {
        if (ShouldRouteThroughBroker(path))
        {
            var read = SandboxBroker.ReadAllBytes(path);
            if (read.Outcome != BrokerOutcome.Ok || read.Bytes.Length == 0)
                return JsonValue.Null;
            return JsonValue.TryParse(Encoding.UTF8.GetString(read.Bytes), out JsonValue value)
                ? value
                : JsonValue.Null;
        }
        return JsonValue.Load(path);
    }

    // Writes the JSON. Returns true when the file lands with every byte the caller passed.
    // A broker write that failed to reach the daemon, that the daemon refused, or that the
    // wire truncated is reported as failure so the caller can surface it instead of silently
    // reporting a settings save that never landed.
    private static bool SaveJson(string path, JsonValue value)
    {
        if (ShouldRouteThroughBroker(path))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value.Write(indented: true));
            return SandboxBroker.WriteAllBytesAtomic(path, bytes) == BrokerOutcome.Ok;
        }
        value.Save(path, indented: true);
        return true;
    }

    // True when the path lands on a partition the module's fpkg sandbox does not bind. Every
    // path returned by the shell's own DataFolder that names one of these partitions is on the
    // daemon side of the file view, so it has to go through the broker. Prefix-matching is
    // strict: "/data" and "/data/..." match, "/database" does not.
    private static bool ShouldRouteThroughBroker(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        return MatchesPartition(path, "/data")
            || MatchesPartition(path, "/user");
    }

    private static bool MatchesPartition(string path, string partition)
    {
        if (!path.StartsWith(partition, StringComparison.Ordinal))
            return false;
        // A partition name matches when the input path is either the partition root or a child
        // of it. A shared prefix that continues into a longer name (e.g. "/database" against
        // "/data") must not match, or the file would land on the wrong routing path.
        return path.Length == partition.Length || path[partition.Length] == '/';
    }

    public void Reset()
    {
        var defaults = new AppSettings();
        StartPath = defaults.StartPath;
        ConfirmDestructive = defaults.ConfirmDestructive;
        ShowHiddenFiles = defaults.ShowHiddenFiles;
        SystemNotifications = defaults.SystemNotifications;
        ToastSeconds = defaults.ToastSeconds;
        TextScale = defaults.TextScale;
        ListRows = defaults.ListRows;
        PlaySoundtrack = defaults.PlaySoundtrack;
        Volume = defaults.Volume;
        PayloadHost = defaults.PayloadHost;
        PayloadPort = defaults.PayloadPort;
        BackupSearchPath = defaults.BackupSearchPath;
    }

    private static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;

    private static float Clamp(float value, float min, float max) =>
        value < min ? min : value > max ? max : value;
}
