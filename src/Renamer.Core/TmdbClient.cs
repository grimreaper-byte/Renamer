using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Renamer.Core;

public class TmdbException(string message) : Exception(message);

/// <summary>TMDB refused the key. Every further request would fail the same way.</summary>
public sealed class TmdbAuthException(string message) : TmdbException(message);

public sealed record TmdbCandidate(
    int Id,
    MediaKind Kind,
    string Title,
    string OriginalTitle,
    string? Date,
    double Popularity,
    string? Overview,
    IReadOnlyList<string> OriginCountries)
{
    public int? Year => Date is { Length: >= 4 } && int.TryParse(Date[..4], out var year) ? year : null;

    public string DisplayTitle => Year is null ? Title : $"{Title} ({Year})";
}

public sealed record TmdbEpisode(int SeasonNumber, int EpisodeNumber, string Name, string? AirDate);

public sealed record TmdbSeasonSummary(int SeasonNumber, int EpisodeCount, string? AirDate);

public sealed record TmdbShow(int Id, string Name, string? FirstAirDate, IReadOnlyList<TmdbSeasonSummary> Seasons);

/// <summary>
/// Client for the parts of the TMDB v3 API the app needs. Accepts either kind of credential TMDB
/// hands out: a v3 API key, sent as a query value, or a v4 read access token, sent as a bearer header.
/// Error messages never contain the request URL, because a v3 key is part of it.
/// </summary>
public sealed class TmdbClient
{
    public const string DefaultBaseUrl = "https://api.themoviedb.org/3";

    private const int MaxConcurrentRequests = 8;
    private const int MaxRateLimitRetries = 6;
    private const int MaxTransientRetries = 2;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _key;
    private readonly bool _isBearer;
    private readonly string _language;
    private readonly string _baseUrl;
    private readonly SemaphoreSlim _throttle = new(MaxConcurrentRequests);
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _cache = new();

    public TmdbClient(HttpClient http, string key, string language, string? baseUrl = null)
    {
        _http = http;
        _key = key.Trim();
        _isBearer = _key.StartsWith("eyJ", StringComparison.Ordinal) || _key.Contains('.');
        _language = string.IsNullOrWhiteSpace(language) ? "en-US" : language.Trim();
        _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl).TrimEnd('/');
    }

    /// <summary>Makes one cheap request. Throws TmdbAuthException if the key is refused.</summary>
    public async Task TestConnectionAsync(CancellationToken ct = default)
    {
        await GetAsync<ConfigurationDto>("/configuration", [], ct);
    }

    public Task<IReadOnlyList<TmdbCandidate>> SearchMoviesAsync(string query, int? year, CancellationToken ct = default) =>
        CachedAsync<IReadOnlyList<TmdbCandidate>>($"movie|{query}|{year}", async () =>
        {
            var page = await GetAsync<SearchPageDto>("/search/movie", [("query", query), ("year", Text(year)), ("include_adult", "false")], ct);
            return page?.Results?.Select(r => new TmdbCandidate(
                r.Id,
                MediaKind.Movie,
                r.Title ?? "",
                r.OriginalTitle ?? r.Title ?? "",
                r.ReleaseDate,
                r.Popularity,
                r.Overview,
                [])).ToList() ?? [];
        });

    public Task<IReadOnlyList<TmdbCandidate>> SearchShowsAsync(string query, int? year, CancellationToken ct = default) =>
        CachedAsync<IReadOnlyList<TmdbCandidate>>($"tv|{query}|{year}", async () =>
        {
            var page = await GetAsync<SearchPageDto>("/search/tv", [("query", query), ("first_air_date_year", Text(year)), ("include_adult", "false")], ct);
            return page?.Results?.Select(r => new TmdbCandidate(
                r.Id,
                MediaKind.Episode,
                r.Name ?? "",
                r.OriginalName ?? r.Name ?? "",
                r.FirstAirDate,
                r.Popularity,
                r.Overview,
                r.OriginCountry ?? [])).ToList() ?? [];
        });

    public Task<TmdbShow?> GetShowAsync(int id, CancellationToken ct = default) =>
        CachedAsync<TmdbShow?>($"show|{id}", async () =>
        {
            var show = await GetAsync<ShowDto>($"/tv/{id}", [], ct);
            return show is null
                ? null
                : new TmdbShow(
                    show.Id,
                    show.Name ?? "",
                    show.FirstAirDate,
                    show.Seasons?.Select(s => new TmdbSeasonSummary(s.SeasonNumber, s.EpisodeCount, s.AirDate)).ToList() ?? []);
        });

    /// <summary>All episodes in a season, or null if TMDB has no such season.</summary>
    public Task<IReadOnlyList<TmdbEpisode>?> GetSeasonAsync(int showId, int season, CancellationToken ct = default) =>
        CachedAsync<IReadOnlyList<TmdbEpisode>?>($"season|{showId}|{season}", async () =>
        {
            var dto = await GetAsync<SeasonDto>($"/tv/{showId}/season/{season}", [], ct);
            return dto?.Episodes?.Select(e => new TmdbEpisode(e.SeasonNumber, e.EpisodeNumber, e.Name ?? "", e.AirDate)).ToList();
        });

    private static string? Text(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private async Task<T> CachedAsync<T>(string key, Func<Task<T>> load)
    {
        var entry = _cache.GetOrAdd(key, _ => new Lazy<Task<object?>>(async () => await load()));
        try
        {
            return (T)(await entry.Value)!;
        }
        catch
        {
            // Keep failures out of the cache so the next attempt asks TMDB again.
            _cache.TryRemove(key, out _);
            throw;
        }
    }

    /// <summary>Returns default when TMDB answers 404.</summary>
    private async Task<T?> GetAsync<T>(string path, (string Name, string? Value)[] query, CancellationToken ct)
        where T : class
    {
        var url = BuildUrl(path, query);
        var rateLimited = 0;
        var transient = 0;

        while (true)
        {
            HttpResponseMessage response;
            await _throttle.WaitAsync(ct);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.ParseAdd("application/json");
                if (_isBearer)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
                }

                response = await _http.SendAsync(request, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                if (transient++ < MaxTransientRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(transient), ct);
                    continue;
                }

                throw new TmdbException("Could not reach TMDB. Check your internet connection.");
            }
            finally
            {
                _throttle.Release();
            }

            using (response)
            {
                switch (response.StatusCode)
                {
                    case HttpStatusCode.Unauthorized:
                        throw new TmdbAuthException("TMDB rejected the API key. Check it in Settings.");

                    case HttpStatusCode.NotFound:
                        return null;

                    case HttpStatusCode.TooManyRequests when rateLimited++ < MaxRateLimitRetries:
                        await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2), ct);
                        continue;

                    case >= HttpStatusCode.InternalServerError when transient++ < MaxTransientRetries:
                        await Task.Delay(TimeSpan.FromSeconds(transient), ct);
                        continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new TmdbException($"TMDB returned an error ({(int)response.StatusCode}).");
                }

                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct);
                }
                catch (JsonException)
                {
                    throw new TmdbException("TMDB sent a response the app could not read.");
                }
            }
        }
    }

    private string BuildUrl(string path, (string Name, string? Value)[] query)
    {
        var url = new StringBuilder(_baseUrl).Append(path).Append("?language=").Append(Uri.EscapeDataString(_language));
        foreach (var (name, value) in query)
        {
            if (!string.IsNullOrEmpty(value))
            {
                url.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
            }
        }

        if (!_isBearer)
        {
            url.Append("&api_key=").Append(Uri.EscapeDataString(_key));
        }

        return url.ToString();
    }

    private sealed class ConfigurationDto;

    private sealed class SearchPageDto
    {
        public List<SearchResultDto>? Results { get; set; }
    }

    private sealed class SearchResultDto
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? OriginalTitle { get; set; }
        public string? ReleaseDate { get; set; }
        public string? Name { get; set; }
        public string? OriginalName { get; set; }
        public string? FirstAirDate { get; set; }
        public double Popularity { get; set; }
        public string? Overview { get; set; }
        public List<string>? OriginCountry { get; set; }
    }

    private sealed class ShowDto
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? FirstAirDate { get; set; }
        public List<SeasonSummaryDto>? Seasons { get; set; }
    }

    private sealed class SeasonSummaryDto
    {
        public int SeasonNumber { get; set; }
        public int EpisodeCount { get; set; }
        public string? AirDate { get; set; }
    }

    private sealed class SeasonDto
    {
        public List<EpisodeDto>? Episodes { get; set; }
    }

    private sealed class EpisodeDto
    {
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        public string? Name { get; set; }
        public string? AirDate { get; set; }
    }
}
