using System.Globalization;
using System.Text.RegularExpressions;

namespace Renamer.Core;

public enum MatchQuality
{
    /// <summary>Confident enough to tick for renaming straight away.</summary>
    Good,

    /// <summary>A best guess the user should look at first.</summary>
    NeedsReview,

    NotFound,
}

public sealed record MatchResult(
    MatchQuality Quality,
    MediaInfo? Info,
    double Confidence,
    string? Message,
    IReadOnlyList<TmdbCandidate> Candidates);

/// <summary>Searches TMDB for a parsed file, scores the results and fills in episode details.</summary>
public sealed class MediaMatcher(TmdbClient tmdb)
{
    public const double GoodScore = 0.82;

    private const int MaxAirDateSeasons = 3;

    private static readonly Regex CountrySuffix = new(
        @"^(?<title>.+?)\s+\(?(?<country>US|USA|UK|AU|NZ|CA)\)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly TmdbClient _tmdb = tmdb;

    public async Task<MatchResult> MatchAsync(ParsedMedia parsed, string sourcePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(parsed.Title))
        {
            return NotFound("Could not work out a title from this file name.");
        }

        var kind = parsed.Kind == MediaKind.Episode ? MediaKind.Episode : MediaKind.Movie;
        var query = parsed.Title;
        string? country = null;

        var candidates = await SearchAsync(kind, query, parsed.Year, ct);
        if (candidates.Count == 0 && kind == MediaKind.Episode && CountrySuffix.Match(parsed.Title) is { Success: true } suffix)
        {
            // "The.Office.US" is how release names tell apart shows TMDB lists under one title.
            query = suffix.Groups["title"].Value;
            country = suffix.Groups["country"].Value.ToUpperInvariant();
            candidates = await SearchAsync(kind, query, parsed.Year, ct);
        }

        if (candidates.Count == 0)
        {
            return NotFound($"TMDB has no {(kind == MediaKind.Movie ? "movie" : "show")} called \"{parsed.Title}\".");
        }

        var ranked = candidates
            .Select(candidate => (Candidate: candidate, Score: Score(query, parsed.Year, country, candidate)))
            .OrderByDescending(entry => entry.Score)
            .ThenByDescending(entry => entry.Candidate.Popularity)
            .ToList();

        var best = ranked[0];
        var resolved = await ResolveAsync(parsed, best.Candidate, sourcePath, ct);
        var ordered = ranked.Select(entry => entry.Candidate).ToList();

        var reason = resolved.Message;
        if (best.Score < GoodScore)
        {
            reason ??= $"\"{parsed.Title}\" is only a loose match for \"{best.Candidate.DisplayTitle}\".";
        }
        else if (country is null && IsAmbiguous(query, parsed.Year, ranked))
        {
            reason ??= $"More than one title is called \"{best.Candidate.Title}\". Check this is the right one.";
        }

        var quality = reason is null ? MatchQuality.Good : MatchQuality.NeedsReview;
        return resolved with { Quality = quality, Confidence = best.Score, Message = reason, Candidates = ordered };
    }

    /// <summary>Builds the result for one chosen TMDB entry. Used for automatic and manual matches.</summary>
    public async Task<MatchResult> ResolveAsync(ParsedMedia parsed, TmdbCandidate candidate, string sourcePath, CancellationToken ct = default)
    {
        var info = new MediaInfo
        {
            Kind = candidate.Kind,
            Title = candidate.Title,
            Year = candidate.Year,
            TmdbId = candidate.Id,
            Resolution = parsed.Resolution,
            Source = parsed.Source,
            Codec = parsed.Codec,
            OriginalName = Path.GetFileNameWithoutExtension(sourcePath),
        };

        if (candidate.Kind == MediaKind.Movie)
        {
            return new MatchResult(MatchQuality.Good, info, 1, null, [candidate]);
        }

        var (season, episode, problem) = await LocateEpisodeAsync(parsed, candidate.Id, ct);
        if (season is null || episode is null)
        {
            return new MatchResult(
                MatchQuality.NeedsReview,
                null,
                1,
                problem ?? "Enter the season and episode for this file.",
                [candidate]);
        }

        var last = parsed.AirDate is null && parsed.EpisodeEnd is { } end && end > episode ? end : episode.Value;
        var numbers = Enumerable.Range(episode.Value, last - episode.Value + 1).ToList();

        var listed = await _tmdb.GetSeasonAsync(candidate.Id, season.Value, ct);
        var found = listed?.Where(e => numbers.Contains(e.EpisodeNumber)).OrderBy(e => e.EpisodeNumber).ToList() ?? [];

        info = info with
        {
            Season = season,
            Episodes = numbers,
            EpisodeTitle = found.Count > 0 ? string.Join(" & ", found.Select(e => e.Name)) : null,
            AirDate = parsed.AirDate ?? ParseDate(found.FirstOrDefault()?.AirDate),
        };

        if (listed is null)
        {
            return new MatchResult(MatchQuality.NeedsReview, info, 1, $"TMDB has no season {season} for this show.", [candidate]);
        }

        return found.Count < numbers.Count
            ? new MatchResult(MatchQuality.NeedsReview, info, 1, $"TMDB does not list S{season:00}E{episode:00} for this show.", [candidate])
            : new MatchResult(MatchQuality.Good, info, 1, null, [candidate]);
    }

    public async Task<IReadOnlyList<TmdbCandidate>> SearchAsync(MediaKind kind, string query, int? year, CancellationToken ct = default)
    {
        var results = await QueryAsync(kind, query, year, ct);
        return results.Count == 0 && year is not null
            ? await QueryAsync(kind, query, null, ct)
            : results;
    }

    private Task<IReadOnlyList<TmdbCandidate>> QueryAsync(MediaKind kind, string query, int? year, CancellationToken ct) =>
        kind == MediaKind.Movie
            ? _tmdb.SearchMoviesAsync(query, year, ct)
            : _tmdb.SearchShowsAsync(query, year, ct);

    private async Task<(int? Season, int? Episode, string? Problem)> LocateEpisodeAsync(ParsedMedia parsed, int showId, CancellationToken ct)
    {
        if (parsed.AirDate is { } date)
        {
            return await FindByAirDateAsync(showId, date, ct);
        }

        if (parsed.Absolute is { } absolute)
        {
            return await FindByAbsoluteAsync(showId, absolute, ct);
        }

        return parsed is { Season: not null, Episode: not null }
            ? (parsed.Season, parsed.Episode, null)
            : (null, null, null);
    }

    /// <summary>Counts through the show's seasons in order, leaving out specials (season 0).</summary>
    private async Task<(int? Season, int? Episode, string? Problem)> FindByAbsoluteAsync(int showId, int absolute, CancellationToken ct)
    {
        var show = await _tmdb.GetShowAsync(showId, ct);
        var remaining = absolute;
        var seasons = show?.Seasons ?? [];
        foreach (var season in seasons.Where(s => s.SeasonNumber > 0).OrderBy(s => s.SeasonNumber))
        {
            if (remaining <= season.EpisodeCount)
            {
                return (season.SeasonNumber, remaining, null);
            }

            remaining -= season.EpisodeCount;
        }

        return (null, null, $"This show has fewer than {absolute} episodes on TMDB.");
    }

    /// <summary>Checks the most recent seasons that had started by that date.</summary>
    private async Task<(int? Season, int? Episode, string? Problem)> FindByAirDateAsync(int showId, DateOnly date, CancellationToken ct)
    {
        var show = await _tmdb.GetShowAsync(showId, ct);
        var seasons = (show?.Seasons ?? [])
            .Where(s => s.SeasonNumber > 0 && (ParseDate(s.AirDate) is not { } start || start <= date))
            .OrderByDescending(s => s.SeasonNumber)
            .Take(MaxAirDateSeasons);

        var wanted = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var season in seasons)
        {
            var episodes = await _tmdb.GetSeasonAsync(showId, season.SeasonNumber, ct);
            var hit = episodes?.FirstOrDefault(e => e.AirDate == wanted);
            if (hit is not null)
            {
                return (season.SeasonNumber, hit.EpisodeNumber, null);
            }
        }

        return (null, null, $"TMDB lists no episode of this show airing on {wanted}.");
    }

    private static double Score(string query, int? year, string? country, TmdbCandidate candidate)
    {
        var score = Math.Max(TitleMatch.Similarity(query, candidate.Title), TitleMatch.Similarity(query, candidate.OriginalTitle));

        if (year is not null && candidate.Year is not null)
        {
            var distance = Math.Abs(year.Value - candidate.Year.Value);
            score += distance switch
            {
                0 => 0.10,
                1 => 0.03,
                _ => -0.25,
            };
        }
        else
        {
            // Popularity only separates titles that are otherwise equal.
            score += Math.Min(Math.Log10(1 + candidate.Popularity) / 30, 0.05);
        }

        if (country is not null)
        {
            var code = country switch
            {
                "UK" => "GB",
                "USA" => "US",
                _ => country,
            };
            score += candidate.OriginCountries.Contains(code, StringComparer.OrdinalIgnoreCase) ? 0.15 : -0.10;
        }

        return Math.Clamp(score, 0, 1);
    }

    private static bool IsAmbiguous(string query, int? year, List<(TmdbCandidate Candidate, double Score)> ranked)
    {
        if (year is not null || ranked.Count < 2)
        {
            return false;
        }

        var first = ranked[0].Candidate;
        var second = ranked.Skip(1).FirstOrDefault(entry =>
            TitleMatch.Similarity(query, entry.Candidate.Title) >= 0.98 ||
            TitleMatch.Similarity(query, entry.Candidate.OriginalTitle) >= 0.98).Candidate;

        return second is not null && second.Popularity >= first.Popularity * 0.5;
    }

    private static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static MatchResult NotFound(string message) => new(MatchQuality.NotFound, null, 0, message, []);
}
