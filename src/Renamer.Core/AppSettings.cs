using System.Text.Json;
using System.Text.Json.Serialization;

namespace Renamer.Core;

public enum RuleStage
{
    BeforeMatching,
    FinalName,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public enum DestinationMode
{
    RenameInPlace,
    MoveToLibrary,
}

public sealed class ParsePattern
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public MediaKind Kind { get; set; } = MediaKind.Episode;
    public string Regex { get; set; } = "";
}

public sealed class RegexRule
{
    public bool Enabled { get; set; } = true;
    public RuleStage Stage { get; set; } = RuleStage.BeforeMatching;
    public string Find { get; set; } = "";
    public string Replace { get; set; } = "";
    public string Note { get; set; } = "";
}

public sealed class NamingTemplates
{
    public string MovieFolder { get; set; } = "";
    public string MovieFile { get; set; } = "";
    public string TvFolder { get; set; } = "";
    public string TvFile { get; set; } = "";

    public NamingTemplates Clone() => (NamingTemplates)MemberwiseClone();

    public bool SameAs(NamingTemplates other) =>
        MovieFolder == other.MovieFolder && MovieFile == other.MovieFile &&
        TvFolder == other.TvFolder && TvFile == other.TvFile;
}

public sealed class AppSettings
{
    /// <summary>Held in memory only. SettingsStore saves it encrypted, never as plain text.</summary>
    [JsonIgnore]
    public string ApiKey { get; set; } = "";

    public string? ApiKeyProtected { get; set; }

    /// <summary>Not shown in the UI. Lets the app be pointed at a stand-in server for testing.</summary>
    public string ApiBaseUrl { get; set; } = TmdbClient.DefaultBaseUrl;

    public AppTheme Theme { get; set; } = AppTheme.System;

    public string Language { get; set; } = "en-US";
    public bool AutoMatch { get; set; } = true;

    public string Preset { get; set; } = Defaults.DefaultPreset;

    public NamingTemplates Templates { get; set; } =
        Defaults.Presets.Single(p => p.Name == Defaults.DefaultPreset).Templates.Clone();

    public DestinationMode Destination { get; set; } = DestinationMode.RenameInPlace;
    public string MovieLibrary { get; set; } = "";
    public string TvLibrary { get; set; } = "";

    public string VideoExtensions { get; set; } = Defaults.VideoExtensions;
    public bool RenameSidecars { get; set; } = true;
    public string SidecarExtensions { get; set; } = Defaults.SidecarExtensions;
    public string IgnorePattern { get; set; } = Defaults.IgnorePattern;

    public List<ParsePattern> Patterns { get; set; } = Defaults.Patterns();
    public List<RegexRule> Rules { get; set; } = Defaults.Rules();

    public AppSettings Clone()
    {
        var copy = FromJson(ToJson());
        copy.ApiKey = ApiKey;
        return copy;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static AppSettings FromJson(string json) =>
        JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
