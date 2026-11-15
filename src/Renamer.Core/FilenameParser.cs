using System.Text.RegularExpressions;

namespace Renamer.Core;

/// <summary>
/// Reads title, year, season and episode from a file path using the configured patterns,
/// falling back on the parent folders when the file name alone is not enough.
/// </summary>
public sealed class FilenameParser
{
    private const RegexOptions Fixed = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex SeasonFolder = new(
        @"^(?:(?<title>.*?)[\s._\-]+)?(?:Season|Series|Saison|Staffel|Temporada|S)[\s._-]*(?<season>\d{1,2})(?![A-Za-z0-9])", Fixed);

    private static readonly Regex SpecialsFolder = new(@"^Specials?$", Fixed);

    private static readonly Regex BareEpisode = new(
        @"^(?:(?:E|Ep|Episode)[\s._-]*)?(?<episode>\d{1,3})(?:[\s._-]*-[\s._-]*(?:E|Ep)?(?<episode_end>\d{1,3})(?![A-Za-z0-9]))?(?:(?![A-Za-z0-9]).*)?$", Fixed);

    private static readonly Regex InlineEpisode = new(
        @"(?:^|[\s._-])(?:E|Ep|Episode)[\s._-]*(?<episode>\d{1,3})(?![A-Za-z0-9])", Fixed);

    private static readonly Regex LeadingEpisode = new(
        @"^(?:E|Ep|Episode)[\s._-]*(?<episode>\d{1,3})(?![A-Za-z0-9])", Fixed);

    private static readonly Regex GenericFolder = new(
        @"^(?:downloads?|videos?|movies?|films?|tv|tv[\s._-]*shows?|shows?|series|media|complete|completed|torrents?|new folder|desktop|temp|tmp|plex|kodi|library|incoming|usenet)$", Fixed);

    private static readonly Regex TitleYearFolder = new(@"^.+\((?:19|20)\d{2}\)$", Fixed);

    private static readonly Regex TrailingYear = new(@"^(?<title>.+?)\s*[(\[]?(?<year>(?:19|20)\d{2})[)\]]?$", Fixed);

    private static readonly Regex DottedInitials = new(@"(?<![A-Za-z0-9'])(?:[A-Za-z] )+[A-Za-z](?![A-Za-z0-9'])", Fixed);

    private static readonly Regex Resolution = new(@"(?<![A-Za-z0-9])(2160p|1080p|1080i|720p|576p|480p|4k|uhd)(?![A-Za-z0-9])", Fixed);

    private static readonly Regex Source = new(@"(?<![A-Za-z0-9])(blu-?ray|bdrip|brrip|remux|web-?dl|webrip|hdtv|hdrip|dvdrip|dvd)(?![A-Za-z0-9])", Fixed);

    private static readonly Regex Codec = new(@"(?<![A-Za-z0-9])(x264|x265|h\.?264|h\.?265|hevc|avc|xvid|divx|av1)(?![A-Za-z0-9])", Fixed);

    private sealed record CompiledPattern(string Name, MediaKind Kind, Regex Regex);

    private sealed record SeasonFolderInfo(int Season, string? Title);

    private readonly List<CompiledPattern> _patterns = [];
    private readonly List<(Regex Regex, string Replace)> _rules;

    public FilenameParser(AppSettings settings)
    {
        foreach (var pattern in settings.Patterns)
        {
            if (!pattern.Enabled)
            {
                continue;
            }

            var regex = RegexTools.TryCreate(pattern.Regex, out _);
            if (regex is not null)
            {
                _patterns.Add(new CompiledPattern(pattern.Name, pattern.Kind, regex));
            }
        }

        _rules = RegexTools.CompileRules(settings.Rules, RuleStage.BeforeMatching);
    }

    /// <summary>Parses a full path, using the parent folders where they add information.</summary>
    public ParsedMedia Parse(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var parsed = ParseName(stem);

        var directory = Path.GetDirectoryName(path);
        var parent = FolderName(directory);
        var grandparent = FolderName(directory is null ? null : Path.GetDirectoryName(directory));
        var season = ReadSeasonFolder(parent);

        if (parsed.Kind == MediaKind.Episode)
        {
            // "Show - 05.mkv" inside "Season 2" is episode 5 of that season, not absolute episode 5.
            if (parsed.Absolute is { } absolute && season is not null)
            {
                parsed = parsed with { Season = season.Season, Episode = absolute, Absolute = null };
            }

            return string.IsNullOrWhiteSpace(parsed.Title)
                ? FillTitleFromFolders(parsed, parent, grandparent, season)
                : parsed;
        }

        var cleanedStem = ApplyRules(stem);

        if (season is not null)
        {
            var match = BareEpisode.Match(cleanedStem);
            if (!match.Success)
            {
                match = InlineEpisode.Match(cleanedStem);
            }

            if (match.Success)
            {
                var fromSeason = parsed with
                {
                    Kind = MediaKind.Episode,
                    Title = "",
                    Year = null,
                    Season = season.Season,
                    Episode = ReadInt(match, "episode"),
                    EpisodeEnd = CheckEnd(ReadInt(match, "episode"), ReadInt(match, "episode_end")),
                    Pattern = "Episode number + season folder",
                };
                return FillTitleFromFolders(fromSeason, parent, grandparent, season);
            }
        }

        if (parsed.Year is null && parent.Length > 0 && !GenericFolder.IsMatch(parent))
        {
            var fromFolder = ParseName(parent);

            // Release folder with an unhelpful file name inside: Some.Show.S01E03.1080p/abc123.mkv
            if (fromFolder is { Kind: MediaKind.Episode, Season: not null, Episode: not null } && fromFolder.Title.Length > 0)
            {
                return WithFileTags(fromFolder, parsed, "folder name");
            }

            if (fromFolder is { Kind: MediaKind.Movie, Year: not null })
            {
                var isReleaseFolder = Resolution.IsMatch(parent) || Source.IsMatch(parent) || Codec.IsMatch(parent);
                var isNamedFolder = TitleYearFolder.IsMatch(parent);
                var isSimilar = TitleMatch.Similarity(parsed.Title, fromFolder.Title) >= 0.6;
                if (isReleaseFolder || isNamedFolder || isSimilar)
                {
                    return WithFileTags(fromFolder, parsed, "folder name");
                }
            }

            // Show Name/Episode 5.mkv with no season folder.
            var wordEpisode = LeadingEpisode.Match(cleanedStem);
            if (wordEpisode.Success)
            {
                var title = ParseName(parent);
                return parsed with
                {
                    Kind = MediaKind.Episode,
                    Title = title.Title,
                    Year = title.Year,
                    Season = 1,
                    Episode = ReadInt(wordEpisode, "episode"),
                    Pattern = "Episode number + show folder",
                };
            }
        }

        return parsed;
    }

    /// <summary>Parses one name on its own, without looking at any folders.</summary>
    public ParsedMedia ParseName(string name)
    {
        var cleaned = ApplyRules(name);
        foreach (var pattern in _patterns)
        {
            Match match;
            try
            {
                match = pattern.Regex.Match(cleaned);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (match.Success && Build(pattern, match) is { } result)
            {
                return WithTags(result, name);
            }
        }

        return WithTags(new ParsedMedia { Kind = MediaKind.Unknown, Title = CleanTitle(cleaned) }, name);
    }

    /// <summary>The file name after the "before matching" clean-up rules have run.</summary>
    public string ApplyRules(string name)
    {
        foreach (var (regex, replace) in _rules)
        {
            name = RegexTools.SafeReplace(regex, name, replace);
        }

        return name;
    }

    private static ParsedMedia? Build(CompiledPattern pattern, Match match)
    {
        var title = CleanTitle(match.Groups["title"].Value);
        var year = ReadInt(match, "year");

        if (pattern.Kind == MediaKind.Movie)
        {
            if (title.Length == 0 || (year is not null && !IsPlausibleYear(year.Value)))
            {
                return null;
            }

            return new ParsedMedia { Kind = MediaKind.Movie, Title = title, Year = year, Pattern = pattern.Name };
        }

        var episode = ReadInt(match, "episode");
        var absolute = ReadInt(match, "absolute");
        var airDate = ReadAirDate(match);
        if (episode is null && absolute is null && airDate is null)
        {
            return null;
        }

        // "Doctor.Who.2005.S01E01" keeps the year out of the title being searched.
        if (year is null)
        {
            var trailing = TrailingYear.Match(title);
            if (trailing.Success && int.TryParse(trailing.Groups["year"].Value, out var found) && IsPlausibleYear(found))
            {
                title = trailing.Groups["title"].Value.Trim();
                year = found;
            }
        }

        return new ParsedMedia
        {
            Kind = MediaKind.Episode,
            Title = title,
            Year = year,
            Season = ReadInt(match, "season"),
            Episode = episode,
            EpisodeEnd = CheckEnd(episode, ReadInt(match, "episode_end")),
            Absolute = absolute,
            AirDate = airDate,
            Pattern = pattern.Name,
        };
    }

    private ParsedMedia FillTitleFromFolders(ParsedMedia parsed, string parent, string grandparent, SeasonFolderInfo? season)
    {
        string? folder;
        if (!string.IsNullOrWhiteSpace(season?.Title))
        {
            folder = season.Title;
        }
        else
        {
            folder = season is not null ? grandparent : parent;
        }

        if (string.IsNullOrWhiteSpace(folder) || GenericFolder.IsMatch(folder))
        {
            return parsed;
        }

        var fromFolder = ParseName(folder);
        return parsed with
        {
            Title = fromFolder.Title,
            Year = parsed.Year ?? fromFolder.Year,
        };
    }

    private static ParsedMedia WithFileTags(ParsedMedia fromFolder, ParsedMedia fromFile, string origin) => fromFolder with
    {
        Resolution = fromFile.Resolution ?? fromFolder.Resolution,
        Source = fromFile.Source ?? fromFolder.Source,
        Codec = fromFile.Codec ?? fromFolder.Codec,
        Pattern = $"{fromFolder.Pattern} ({origin})",
    };

    private static ParsedMedia WithTags(ParsedMedia parsed, string name) => parsed with
    {
        Resolution = FirstCapture(Resolution, name),
        Source = FirstCapture(Source, name),
        Codec = FirstCapture(Codec, name),
    };

    private static string? FirstCapture(Regex regex, string text)
    {
        var match = regex.Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static SeasonFolderInfo? ReadSeasonFolder(string folder)
    {
        if (folder.Length == 0)
        {
            return null;
        }

        if (SpecialsFolder.IsMatch(folder))
        {
            return new SeasonFolderInfo(0, null);
        }

        var match = SeasonFolder.Match(folder);
        return match.Success && int.TryParse(match.Groups["season"].Value, out var season)
            ? new SeasonFolderInfo(season, match.Groups["title"].Success ? match.Groups["title"].Value : null)
            : null;
    }

    private static string FolderName(string? directory) =>
        string.IsNullOrEmpty(directory) ? "" : Path.GetFileName(directory.TrimEnd('\\', '/'));

    private static int? ReadInt(Match match, string group)
    {
        var value = match.Groups[group];
        return value.Success && int.TryParse(value.Value, out var number) ? number : null;
    }

    private static DateOnly? ReadAirDate(Match match)
    {
        var year = ReadInt(match, "air_year");
        var month = ReadInt(match, "air_month");
        var day = ReadInt(match, "air_day");
        if (year is null || month is null || day is null || !IsPlausibleYear(year.Value))
        {
            return null;
        }

        return day <= DateTime.DaysInMonth(year.Value, month.Value)
            ? new DateOnly(year.Value, month.Value, day.Value)
            : null;
    }

    private static int? CheckEnd(int? first, int? last) =>
        first is not null && last is not null && last > first && last - first <= 50 ? last : null;

    private static bool IsPlausibleYear(int year) => year >= 1900 && year <= DateTime.Now.Year + 1;

    internal static string CleanTitle(string raw)
    {
        var text = Regex.Replace(raw, @"\[[^\]]*\]", " ");
        var hadDots = text.Contains('.');
        text = text.Replace('_', ' ').Replace('.', ' ');
        text = Regex.Replace(text, @"\s+", " ").Trim();

        // Dots were word separators above, so "S.W.A.T." came through as "S W A T".
        if (hadDots)
        {
            text = DottedInitials.Replace(text, m => m.Value.Replace(" ", ".") + ".");
        }

        return text.Trim(' ', '-', '–', '(', '[', ',', '+');
    }
}
