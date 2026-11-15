namespace Renamer.Core;

public sealed record NamingPreset(string Name, NamingTemplates Templates);

public static class Defaults
{
    public const string CustomPreset = "Custom";

    public const string VideoExtensions =
        ".mkv, .mp4, .avi, .mov, .m4v, .wmv, .mpg, .mpeg, .ts, .m2ts, .webm, .flv, .vob, .divx, .ogv";

    public const string SidecarExtensions = ".srt, .sub, .idx, .ass, .ssa, .vtt, .smi";

    /// <summary>Matched against the file name and its parent folder when files are added.</summary>
    public const string IgnorePattern = @"(?:^|[\s._\-(\[])(?:sample|trailer)(?:[\s._\-)\]]|$)";

    /// <summary>
    /// Plex: https://support.plex.tv/articles/naming-and-organizing-your-tv-show-files/
    /// Kodi lists every episode of a multi-episode file (S01E01E02) because its scanner
    /// reads each number it sees rather than treating two numbers as a range.
    /// </summary>
    public static readonly IReadOnlyList<NamingPreset> Presets =
    [
        new("Reelhouse", new NamingTemplates
        {
            MovieFolder = "{title} ({year})",
            MovieFile = "{title} ({year})",
            TvFolder = "{show} ({year})/Series {season}",
            TvFile = "{show} ({year}) - {season}x{episode:00}{episode_to} - {episode_title}",
        }),
        new("Plex", new NamingTemplates
        {
            MovieFolder = "{title} ({year})",
            MovieFile = "{title} ({year})",
            TvFolder = "{show} ({year})/Season {season:00}",
            TvFile = "{show} ({year}) - s{season:00}e{episode:00}{episode_range} - {episode_title}",
        }),
        new("Plex + TMDB ID", new NamingTemplates
        {
            MovieFolder = "{title} ({year}) {tmdb-{tmdb_id}}",
            MovieFile = "{title} ({year}) {tmdb-{tmdb_id}}",
            TvFolder = "{show} ({year}) {tmdb-{tmdb_id}}/Season {season:00}",
            TvFile = "{show} ({year}) - s{season:00}e{episode:00}{episode_range} - {episode_title}",
        }),
        new("Kodi", new NamingTemplates
        {
            MovieFolder = "{title} ({year})",
            MovieFile = "{title} ({year})",
            TvFolder = "{show} ({year})/Season {season:00}",
            TvFile = "{show} S{season:00}E{episode:00}{episode_list} - {episode_title}",
        }),
    ];

    /// <summary>The format new installs start with.</summary>
    public const string DefaultPreset = "Reelhouse";

    /// <summary>Earlier versions of presets. Settings saved with one are moved to the current version.</summary>
    private static readonly IReadOnlyList<NamingPreset> RetiredPresets =
    [
        new("Reelhouse", new NamingTemplates
        {
            MovieFolder = "{title} ({year})",
            MovieFile = "{title} ({year})",
            TvFolder = "{show} ({year})/Series {season}",
            TvFile = "{show} ({year}) - {season}x{episode:00} - {episode_title}",
        }),
    ];

    /// <summary>The preset these templates match exactly, or "Custom" if they match none.</summary>
    public static string PresetNameFor(NamingTemplates templates) =>
        Presets.FirstOrDefault(p => p.Templates.SameAs(templates))?.Name ?? CustomPreset;

    /// <summary>The current templates of a preset if these are an earlier version of it, otherwise these.</summary>
    public static NamingTemplates Upgrade(NamingTemplates templates)
    {
        var retired = RetiredPresets.FirstOrDefault(p => p.Templates.SameAs(templates));
        return retired is null
            ? templates
            : Presets.Single(p => p.Name == retired.Name).Templates.Clone();
    }

    /// <summary>
    /// Tried top to bottom, first match wins. Groups the app reads: title, year, season,
    /// episode, episode_end, absolute, air_year, air_month, air_day.
    /// </summary>
    public static List<ParsePattern> Patterns() =>
    [
        new()
        {
            Name = "S01E02 or S01E02E03",
            Kind = MediaKind.Episode,
            Regex = @"^(?<title>.*?)(?:^|[\s._\-(\[]+)S(?<season>\d{1,4})[\s._-]*E(?<episode>\d{1,4})(?:(?:[\s._]*-[\s._]*|[\s._]*)E(?<episode_end>\d{1,4})|-(?<episode_end>\d{1,4})(?![\dip]))*(?![A-Za-z0-9])",
        },
        new()
        {
            Name = "1x02 or 1x02-03",
            Kind = MediaKind.Episode,
            Regex = @"^(?<title>.*?)(?:^|[\s._\-(\[]+)(?<season>\d{1,2})x(?<episode>\d{2,3})(?:[\s._-]*x(?<episode_end>\d{2,3})|-(?<episode_end>\d{2,3})(?![\dip]))*(?![A-Za-z0-9])",
        },
        new()
        {
            Name = "Season 1 Episode 2",
            Kind = MediaKind.Episode,
            Regex = @"^(?<title>.*?)(?:^|[\s._\-(\[]+)Season[\s._-]*(?<season>\d{1,2})[\s._-]*(?:Episode|Ep\.?|E)[\s._-]*(?<episode>\d{1,3})(?![A-Za-z0-9])",
        },
        new()
        {
            Name = "Air date (2024.03.15)",
            Kind = MediaKind.Episode,
            Regex = @"^(?<title>.+?)[\s._\-(\[]+(?<air_year>(?:19|20)\d{2})[\s._-](?<air_month>0[1-9]|1[0-2])[\s._-](?<air_day>0[1-9]|[12]\d|3[01])(?![A-Za-z0-9])",
        },
        new()
        {
            Name = "Show - 12 (anime)",
            Kind = MediaKind.Episode,
            Regex = @"^(?!.*[(\[](?:19|20)\d{2}[)\]])(?<title>.+?)[\s._]+-[\s._]+(?:E|EP|Episode[\s._]*)?(?<absolute>(?!(?:19|20)\d{2}(?!\d))\d{1,4})(?:v\d)?(?=[\s._\[(]|$)",
        },
        new()
        {
            Enabled = false,
            Name = "Compact number (Show.102)",
            Kind = MediaKind.Episode,
            Regex = @"^(?<title>.+?)[\s._-]+(?<season>[1-9])(?<episode>\d{2})(?=[\s._\-\[(]|$)",
        },
        new()
        {
            Name = "Movie with year",
            Kind = MediaKind.Movie,
            Regex = @"^(?<title>.+)[\s._\-(\[]+(?<year>(?:19|20)\d{2})(?=[\s._\-)\]]|$)",
        },
        new()
        {
            Name = "Movie without year",
            Kind = MediaKind.Movie,
            Regex = @"^(?<title>.+?)(?:[\s._\-(\[]+(?:2160p|1080p|1080i|720p|576p|480p|4k|uhd|bluray|blu-ray|bdrip|brrip|dvdrip|dvdscr|webrip|web-dl|webdl|hdtv|hdrip|x264|x265|h\.?264|h\.?265|hevc|xvid|remux|proper|repack|extended|unrated|remastered|directors[\s._]cut|imax)(?![A-Za-z0-9]).*)?$",
        },
    ];

    public static List<RegexRule> Rules() =>
    [
        new()
        {
            Stage = RuleStage.BeforeMatching,
            Find = @"^\[?\s*www\.[\w-]+\.[a-z]{2,4}\s*\]?[\s._-]*",
            Replace = "",
            Note = "Remove a website name at the start",
        },
        new()
        {
            Stage = RuleStage.BeforeMatching,
            Find = @"^\[[^\]]+\][\s._-]*",
            Replace = "",
            Note = "Remove a [release group] at the start",
        },
        new()
        {
            Enabled = false,
            Stage = RuleStage.FinalName,
            Find = @"\s&\s",
            Replace = " and ",
            Note = "Example: write & as 'and' in new names",
        },
    ];
}
