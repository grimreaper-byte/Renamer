using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Renamer.Core;

public sealed record NamePlan(string DestinationPath, string DisplayName);

/// <summary>Turns confirmed metadata into a Windows-safe path using the naming templates.</summary>
public sealed class NameBuilder
{
    /// <summary>Longest name stem kept, leaving room for the extension and subtitle language tags.</summary>
    private const int MaxStemLength = 230;

    public static readonly IReadOnlyList<string> KnownTokens =
    [
        "title", "show", "year", "season", "episode", "episode_end", "episode_range", "episode_list",
        "episode_to", "episode_title", "air_date", "tmdb_id", "resolution", "source", "codec", "original_name",
    ];

    private static readonly Regex Token = new(
        @"\{(?<name>[a-z_]+)(?::(?<format>[^{}]+))?\}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly AppSettings _settings;
    private readonly List<(Regex Regex, string Replace)> _finalRules;

    public NameBuilder(AppSettings settings)
    {
        _settings = settings;
        _finalRules = RegexTools.CompileRules(settings.Rules, RuleStage.FinalName);
    }

    public NamePlan? Build(MediaInfo info, string sourcePath, out string? error)
    {
        error = null;
        var stem = RenderFile(info);
        if (stem.Length == 0)
        {
            error = "The file name template produced an empty name.";
            return null;
        }

        var fileName = stem + Path.GetExtension(sourcePath);

        if (_settings.Destination == DestinationMode.RenameInPlace)
        {
            var directory = Path.GetDirectoryName(sourcePath) ?? "";
            return new NamePlan(Path.Combine(directory, fileName), fileName);
        }

        var isMovie = info.Kind == MediaKind.Movie;
        var root = isMovie ? _settings.MovieLibrary : _settings.TvLibrary;
        if (string.IsNullOrWhiteSpace(root))
        {
            error = $"Set the {(isMovie ? "movie" : "TV")} library folder in Settings, or switch to renaming in place.";
            return null;
        }

        var folder = RenderFolder(info);
        var fullRoot = Path.GetFullPath(root);
        var destination = Path.GetFullPath(Path.Combine(fullRoot, folder, fileName));

        var rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            error = "The folder template points outside the library folder.";
            return null;
        }

        return new NamePlan(destination, Path.Combine(folder, fileName));
    }

    public string RenderFile(MediaInfo info)
    {
        var template = info.Kind == MediaKind.Movie ? _settings.Templates.MovieFile : _settings.Templates.TvFile;
        return Finish(CleanSegment(Replace(template, info)), MaxStemLength);
    }

    public string RenderFolder(MediaInfo info)
    {
        var template = info.Kind == MediaKind.Movie ? _settings.Templates.MovieFolder : _settings.Templates.TvFolder;
        var segments = Replace(template, info)
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => Finish(CleanSegment(segment), 200))
            .Where(segment => segment.Length > 0);
        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    public static IEnumerable<string> FindUnknownTokens(string template) =>
        Token.Matches(template)
            .Select(match => match.Groups["name"].Value.ToLowerInvariant())
            .Where(name => !KnownTokens.Contains(name))
            .Distinct();

    /// <summary>
    /// Replaces characters Windows forbids in a name. Runs on every value from TMDB before it goes
    /// into a template, so a title such as "Face/Off" cannot create an extra folder.
    /// </summary>
    public static string SanitizeValue(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case ':':
                    builder.Append(" -");
                    break;
                case '/' or '\\':
                    builder.Append('-');
                    break;
                case '"':
                    builder.Append('\'');
                    break;
                case '?' or '*' or '<' or '>' or '|':
                    break;
                default:
                    if (!char.IsControl(c))
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private string Finish(string text, int maxLength)
    {
        foreach (var (regex, replace) in _finalRules)
        {
            text = RegexTools.SafeReplace(regex, text, replace);
        }

        // A clean-up rule can put anything back, so the safety pass runs after the rules.
        text = CleanSegment(text);
        if (text.Length > maxLength)
        {
            text = CleanSegment(text[..maxLength]);
        }

        return ReservedNames.Contains(text.Split('.')[0]) ? "_" + text : text;
    }

    private static string CleanSegment(string text)
    {
        text = SanitizeValue(text);
        text = Regex.Replace(text, @"\(\s*\)|\[\s*\]", " ");
        text = Regex.Replace(text, @"\s+", " ");
        text = Regex.Replace(text, @"(?:\s-){2,}\s", " - ");
        return text.Trim(' ', '-', '–', '_').TrimEnd('.', ' ', '-');
    }

    private static string Replace(string template, MediaInfo info) =>
        Token.Replace(template, match =>
        {
            var name = match.Groups["name"].Value.ToLowerInvariant();
            var format = match.Groups["format"].Success ? match.Groups["format"].Value : null;
            return Resolve(name, format, info) is { } value ? SanitizeValue(value) : KeepUnknown(name, match.Value);
        });

    private static string KeepUnknown(string name, string literal) => KnownTokens.Contains(name) ? "" : literal;

    private static string? Resolve(string name, string? format, MediaInfo info)
    {
        var first = info.Episodes.Count > 0 ? info.Episodes[0] : (int?)null;
        var last = info.Episodes.Count > 1 ? info.Episodes[^1] : (int?)null;
        return name switch
        {
            "title" or "show" => info.Title,
            "year" => Number(info.Year, format),
            "season" => Number(info.Season, format),
            "episode" => Number(first, format),
            "episode_end" => Number(last ?? first, format),
            "episode_range" => last is null ? "" : "-e" + Number(last, format ?? "00"),
            "episode_list" => string.Concat(info.Episodes.Skip(1).Select(e => "E" + Number(e, format ?? "00"))),
            "episode_to" => last is null ? "" : "-" + Number(last, format ?? "00"),
            "episode_title" => info.EpisodeTitle,
            "air_date" => info.AirDate?.ToString(format ?? "yyyy-MM-dd", CultureInfo.InvariantCulture),
            "tmdb_id" => Number(info.TmdbId, format),
            "resolution" => info.Resolution,
            "source" => info.Source,
            "codec" => info.Codec,
            "original_name" => info.OriginalName,
            _ => null,
        };
    }

    private static string? Number(int? value, string? format)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return value.Value.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return value.Value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
