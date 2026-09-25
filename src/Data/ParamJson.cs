// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using SharpProspero.Storage;
using System;
using System.Collections.Generic;

namespace ProsperoMultiTools.Data;

internal sealed class LocalizedTitle
{
    private JsonValue _source = JsonValue.NewObject();

    public string Language { get; set; } = "";

    public string TitleName
    {
        get => _source.GetString("titleName");
        set => _source["titleName"] = value;
    }

    /// <summary>The full JSON object for this language entry, preserving all fields.</summary>
    internal JsonValue Source => _source;

    internal static LocalizedTitle FromJson(string language, JsonValue langObj)
    {
        var lt = new LocalizedTitle { Language = language };
        lt._source = langObj;
        return lt;
    }
}

internal sealed class ParamJson
{
    private JsonValue _source = JsonValue.Null;

    public string TitleId { get; set; } = "";
    public string ContentId { get; set; } = "";
    public string ContentVersion { get; set; } = "";
    public string MasterVersion { get; set; } = "";
    public string RequiredSystemSoftwareVersion { get; set; } = "";
    public string ApplicationDrmType { get; set; } = "";
    public int ApplicationCategoryType { get; set; }
    public int Attribute { get; set; }
    public int Attribute2 { get; set; }
    public int Attribute3 { get; set; }
    public string DefaultLanguage { get; set; } = "en-US";
    public List<LocalizedTitle> LocalizedTitles { get; } = [];
    public Dictionary<string, int> AgeLevels { get; } = new();
    public string CreationDate { get; set; } = "";
    public string ToolVersion { get; set; } = "";
    public string VersionFileUri { get; set; } = "";

    public string DisplayTitle
    {
        get
        {
            foreach (LocalizedTitle lt in LocalizedTitles)
            {
                if (lt.Language == DefaultLanguage)
                    return lt.TitleName;
            }
            if (LocalizedTitles.Count > 0)
                return LocalizedTitles[0].TitleName;
            return TitleId;
        }
    }

    public static ParamJson? ReadFromFile(string path)
    {
        try
        {
            JsonValue json = JsonValue.Load(path);
            if (json.IsNull)
                return null;
            return ReadFromJson(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ParamJson? ReadFromJson(JsonValue json)
    {
        if (json.IsNull || json.Type != JsonType.Object)
            return null;

        var param = new ParamJson
        {
            TitleId = json.GetString("titleId"),
            ContentId = json.GetString("contentId"),
            ContentVersion = json.GetString("contentVersion"),
            MasterVersion = json.GetString("masterVersion"),
            RequiredSystemSoftwareVersion = json.GetString("requiredSystemSoftwareVersion"),
            ApplicationDrmType = json.GetString("applicationDrmType"),
            ApplicationCategoryType = json.GetInt("applicationCategoryType"),
            Attribute = json.GetInt("attribute"),
            Attribute2 = json.GetInt("attribute2"),
            Attribute3 = json.GetInt("attribute3"),
            VersionFileUri = json.GetString("versionFileUri"),
        };
        param._source = json;

        JsonValue localized = json["localizedParameters"];
        if (localized.Type == JsonType.Object)
        {
            param.DefaultLanguage = localized.GetString("defaultLanguage", "en-US");
            foreach (string lang in localized.Keys)
            {
                if (lang == "defaultLanguage")
                    continue;
                JsonValue langObj = localized[lang];
                if (langObj.Type == JsonType.Object)
                    param.LocalizedTitles.Add(LocalizedTitle.FromJson(lang, langObj));
            }
        }

        JsonValue ageLevel = json["ageLevel"];
        if (ageLevel.Type == JsonType.Object)
        {
            foreach (string key in ageLevel.Keys)
                param.AgeLevels[key] = ageLevel.GetInt(key);
        }

        JsonValue pubtools = json["pubtools"];
        if (pubtools.Type == JsonType.Object)
        {
            param.CreationDate = pubtools.GetString("creationDate");
            param.ToolVersion = pubtools.GetString("toolVersion");
        }

        return param;
    }

    public static ParamJson? ReadFromBytes(byte[] data)
    {
        if (data.Length == 0)
            return null;
        string text = System.Text.Encoding.UTF8.GetString(data);
        if (!JsonValue.TryParse(text, out JsonValue json))
            return null;
        return ReadFromJson(json);
    }

    public JsonValue ToJson()
    {
        var json = JsonValue.NewObject();

        // Copy all keys from the parsed source to preserve unknown fields and key order.
        if (_source.Type == JsonType.Object)
        {
            foreach (string key in _source.Keys)
                json[key] = _source[key];
        }

        // Overwrite mapped keys with current property values.
        json["titleId"] = TitleId;
        json["contentId"] = ContentId;
        json["contentVersion"] = ContentVersion;
        json["masterVersion"] = MasterVersion;
        json["requiredSystemSoftwareVersion"] = RequiredSystemSoftwareVersion;
        json["applicationDrmType"] = ApplicationDrmType;
        json["applicationCategoryType"] = ApplicationCategoryType;
        json["attribute"] = Attribute;
        json["attribute2"] = Attribute2;
        json["attribute3"] = Attribute3;

        // Build localizedParameters, preserving unknown language entries and sub-fields.
        var localized = JsonValue.NewObject();
        JsonValue sourceLocalized = _source["localizedParameters"];
        if (sourceLocalized.Type == JsonType.Object)
        {
            foreach (string key in sourceLocalized.Keys)
                localized[key] = sourceLocalized[key];
        }
        localized["defaultLanguage"] = DefaultLanguage;
        foreach (LocalizedTitle lt in LocalizedTitles)
            localized[lt.Language] = lt.Source;
        json["localizedParameters"] = localized;

        // Build ageLevel, preserving key order from the source.
        var ageLevelObj = JsonValue.NewObject();
        JsonValue sourceAgeLevel = _source["ageLevel"];
        if (sourceAgeLevel.Type == JsonType.Object)
        {
            foreach (string key in sourceAgeLevel.Keys)
                ageLevelObj[key] = sourceAgeLevel[key];
        }
        foreach (var kvp in AgeLevels)
            ageLevelObj[kvp.Key] = kvp.Value;
        json["ageLevel"] = ageLevelObj;

        // Build pubtools, preserving unknown fields.
        var pubtools = JsonValue.NewObject();
        JsonValue sourcePubtools = _source["pubtools"];
        if (sourcePubtools.Type == JsonType.Object)
        {
            foreach (string key in sourcePubtools.Keys)
                pubtools[key] = sourcePubtools[key];
        }
        pubtools["creationDate"] = CreationDate;
        pubtools["toolVersion"] = ToolVersion;
        json["pubtools"] = pubtools;

        json["versionFileUri"] = VersionFileUri;

        return json;
    }

    public void WriteToFile(string path) => ToJson().Save(path, indented: true);
}
