using System.Net;
using System.Text;
using Renamer.Core;

namespace Renamer.Tests;

public class MediaMatcherTests
{
    private const string Key = "abc123";

    /// <summary>Stands in for TMDB. Routes are matched on the start of the path and query.</summary>
    private sealed class FakeTmdb : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = [];
        public List<HttpRequestMessage> Requests { get; } = [];

        public void Json(string route, string body) => Routes[route] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            var path = request.RequestUri!.PathAndQuery;
            var route = Routes.Keys.Where(path.Contains).OrderByDescending(r => r.Length).FirstOrDefault();
            return Task.FromResult(route is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Routes[route]());
        }
    }

    private readonly FakeTmdb _server = new();

    private MediaMatcher Matcher(string key = Key) =>
        new(new TmdbClient(new HttpClient(_server), key, "en-US", "https://tmdb.test/3"));

    private TmdbClient Client(string key = Key) =>
        new(new HttpClient(_server), key, "en-US", "https://tmdb.test/3");

    private static ParsedMedia Show(string title, int season, int episode, int? end = null, int? year = null) => new()
    {
        Kind = MediaKind.Episode,
        Title = title,
        Season = season,
        Episode = episode,
        EpisodeEnd = end,
        Year = year,
    };

    private const string BreakingBadSearch = """
        {"results":[{"id":1396,"name":"Breaking Bad","original_name":"Breaking Bad","first_air_date":"2008-01-20","popularity":300.5,"origin_country":["US"],"overview":"A teacher."}]}
        """;

    private const string BreakingBadSeason2 = """
        {"episodes":[
          {"season_number":2,"episode_number":5,"name":"Breakage","air_date":"2009-04-05"},
          {"season_number":2,"episode_number":6,"name":"Peekaboo","air_date":"2009-04-12"}
        ]}
        """;

    [Fact]
    public async Task MatchesAnEpisodeWithItsTitle()
    {
        _server.Json("/search/tv", BreakingBadSearch);
        _server.Json("/tv/1396/season/2", BreakingBadSeason2);

        var result = await Matcher().MatchAsync(Show("Breaking Bad", 2, 5), @"D:\x\file.mkv");

        Assert.Equal(MatchQuality.Good, result.Quality);
        Assert.Equal("Breaking Bad", result.Info!.Title);
        Assert.Equal(2008, result.Info.Year);
        Assert.Equal(1396, result.Info.TmdbId);
        Assert.Equal("Breakage", result.Info.EpisodeTitle);
        Assert.Equal([5], result.Info.Episodes);
        Assert.Equal("file", result.Info.OriginalName);
    }

    [Fact]
    public async Task MultiEpisodeFilesJoinTheirTitles()
    {
        _server.Json("/search/tv", BreakingBadSearch);
        _server.Json("/tv/1396/season/2", BreakingBadSeason2);

        var result = await Matcher().MatchAsync(Show("Breaking Bad", 2, 5, end: 6), @"D:\x\file.mkv");

        Assert.Equal("Breakage & Peekaboo", result.Info!.EpisodeTitle);
        Assert.Equal([5, 6], result.Info.Episodes);
    }

    [Fact]
    public async Task AnEpisodeTmdbDoesNotListNeedsReviewButStillHasAName()
    {
        _server.Json("/search/tv", BreakingBadSearch);
        _server.Json("/tv/1396/season/2", BreakingBadSeason2);

        var result = await Matcher().MatchAsync(Show("Breaking Bad", 2, 99), @"D:\x\file.mkv");

        Assert.Equal(MatchQuality.NeedsReview, result.Quality);
        Assert.Contains("S02E99", result.Message);
        Assert.Null(result.Info!.EpisodeTitle);
    }

    [Fact]
    public async Task YearPicksTheRightMovie()
    {
        _server.Json("/search/movie", """
            {"results":[
              {"id":1,"title":"Dune","original_title":"Dune","release_date":"2021-09-15","popularity":200},
              {"id":2,"title":"Dune","original_title":"Dune","release_date":"1984-12-14","popularity":40}
            ]}
            """);

        var parsed = new ParsedMedia { Kind = MediaKind.Movie, Title = "Dune", Year = 1984 };
        var result = await Matcher().MatchAsync(parsed, @"D:\x\Dune.mkv");

        Assert.Equal(MatchQuality.Good, result.Quality);
        Assert.Equal(2, result.Info!.TmdbId);
        Assert.Equal(1984, result.Info.Year);
    }

    [Fact]
    public async Task SearchIsRepeatedWithoutTheYearWhenItFindsNothing()
    {
        _server.Json("/search/movie?language=en-US&query=Some%20Film&year=2011", """{"results":[]}""");
        _server.Json("/search/movie?language=en-US&query=Some%20Film&include_adult", """
            {"results":[{"id":5,"title":"Some Film","release_date":"2012-02-01","popularity":3}]}
            """);

        var parsed = new ParsedMedia { Kind = MediaKind.Movie, Title = "Some Film", Year = 2011 };
        var result = await Matcher().MatchAsync(parsed, @"D:\x\y.mkv");

        Assert.Equal(5, result.Info!.TmdbId);
        Assert.Equal(MatchQuality.Good, result.Quality);
    }

    [Fact]
    public async Task ALooseTitleMatchNeedsReview()
    {
        _server.Json("/search/movie", """
            {"results":[{"id":9,"title":"Completely Different Name","release_date":"2001-01-01","popularity":3}]}
            """);

        var parsed = new ParsedMedia { Kind = MediaKind.Movie, Title = "Some Film" };
        var result = await Matcher().MatchAsync(parsed, @"D:\x\y.mkv");

        Assert.Equal(MatchQuality.NeedsReview, result.Quality);
        Assert.NotNull(result.Info);
    }

    [Fact]
    public async Task SameNamedShowsOfSimilarPopularityNeedReview()
    {
        _server.Json("/search/tv", """
            {"results":[
              {"id":1,"name":"Skins","first_air_date":"2007-01-25","popularity":50,"origin_country":["GB"]},
              {"id":2,"name":"Skins","first_air_date":"2011-01-17","popularity":40,"origin_country":["US"]}
            ]}
            """);
        _server.Json("/tv/1/season/1", """{"episodes":[{"episode_number":1,"season_number":1,"name":"Tony"}]}""");

        var result = await Matcher().MatchAsync(Show("Skins", 1, 1), @"D:\x\y.mkv");

        Assert.Equal(MatchQuality.NeedsReview, result.Quality);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Theory]
    [InlineData("The Office US", 2316)]
    [InlineData("The Office UK", 2996)]
    [InlineData("The Office (UK)", 2996)]
    public async Task CountrySuffixPicksTheRightShow(string title, int expected)
    {
        _server.Json("/search/tv?language=en-US&query=The%20Office&", """
            {"results":[
              {"id":2316,"name":"The Office","first_air_date":"2005-03-24","popularity":300,"origin_country":["US"]},
              {"id":2996,"name":"The Office","first_air_date":"2001-07-09","popularity":40,"origin_country":["GB"]}
            ]}
            """);
        _server.Json("/search/tv", """{"results":[]}""");
        _server.Json($"/tv/{expected}/season/1", """{"episodes":[{"episode_number":1,"season_number":1,"name":"Pilot"}]}""");

        var result = await Matcher().MatchAsync(Show(title, 1, 1), @"D:\x\y.mkv");

        Assert.Equal(expected, result.Info!.TmdbId);
        Assert.Equal(MatchQuality.Good, result.Quality);
    }

    [Fact]
    public async Task AbsoluteNumbersAreCountedThroughTheSeasons()
    {
        _server.Json("/search/tv", """{"results":[{"id":7,"name":"Anime","first_air_date":"2010-01-01","popularity":10}]}""");
        _server.Json("/tv/7?", """
            {"id":7,"name":"Anime","first_air_date":"2010-01-01","seasons":[
              {"season_number":0,"episode_count":5,"air_date":"2010-01-01"},
              {"season_number":1,"episode_count":12,"air_date":"2010-01-01"},
              {"season_number":2,"episode_count":12,"air_date":"2011-01-01"}
            ]}
            """);
        _server.Json("/tv/7/season/2", """{"episodes":[{"episode_number":3,"season_number":2,"name":"Third"}]}""");

        var parsed = new ParsedMedia { Kind = MediaKind.Episode, Title = "Anime", Absolute = 15 };
        var result = await Matcher().MatchAsync(parsed, @"D:\x\y.mkv");

        Assert.Equal(2, result.Info!.Season);
        Assert.Equal([3], result.Info.Episodes);
        Assert.Equal("Third", result.Info.EpisodeTitle);
    }

    [Fact]
    public async Task AirDatesFindTheEpisode()
    {
        _server.Json("/search/tv", """{"results":[{"id":8,"name":"Daily","first_air_date":"2000-01-01","popularity":10}]}""");
        _server.Json("/tv/8?", """
            {"id":8,"name":"Daily","seasons":[
              {"season_number":29,"episode_count":100,"air_date":"2024-01-08"},
              {"season_number":30,"episode_count":100,"air_date":"2025-01-06"}
            ]}
            """);
        _server.Json("/tv/8/season/29", """{"episodes":[{"episode_number":42,"season_number":29,"name":"Guest","air_date":"2024-03-15"}]}""");

        var parsed = new ParsedMedia { Kind = MediaKind.Episode, Title = "Daily", AirDate = new DateOnly(2024, 3, 15) };
        var result = await Matcher().MatchAsync(parsed, @"D:\x\y.mkv");

        Assert.Equal(29, result.Info!.Season);
        Assert.Equal([42], result.Info.Episodes);
        Assert.Equal("Guest", result.Info.EpisodeTitle);
    }

    [Fact]
    public async Task NoResultsIsReportedNotThrown()
    {
        _server.Json("/search/movie", """{"results":[]}""");

        var parsed = new ParsedMedia { Kind = MediaKind.Movie, Title = "Nothing" };
        var result = await Matcher().MatchAsync(parsed, @"D:\x\y.mkv");

        Assert.Equal(MatchQuality.NotFound, result.Quality);
        Assert.Null(result.Info);
    }

    [Fact]
    public async Task ManualMatchNeedsSeasonAndEpisode()
    {
        var candidate = new TmdbCandidate(1, MediaKind.Episode, "Show", "Show", null, 0, null, []);

        var result = await Matcher().ResolveAsync(new ParsedMedia { Title = "Whatever" }, candidate, @"D:\x\y.mkv");

        Assert.Equal(MatchQuality.NeedsReview, result.Quality);
        Assert.Null(result.Info);
    }

    [Fact]
    public async Task ARejectedKeyThrowsAuthErrorWithoutTheKey()
    {
        _server.Routes["/configuration"] = () => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var error = await Assert.ThrowsAsync<TmdbAuthException>(() => Client("secret-key").TestConnectionAsync());

        Assert.DoesNotContain("secret-key", error.Message);
    }

    [Fact]
    public async Task V3KeysGoInTheQueryAndV4TokensInTheHeader()
    {
        _server.Json("/configuration", "{}");

        await Client("abcdef0123456789").TestConnectionAsync();
        await Client("eyJhbGciOi.payload.signature").TestConnectionAsync();

        var v3 = _server.Requests[0];
        Assert.Contains("api_key=abcdef0123456789", v3.RequestUri!.Query);
        Assert.Null(v3.Headers.Authorization);

        var v4 = _server.Requests[1];
        Assert.DoesNotContain("api_key", v4.RequestUri!.Query);
        Assert.Equal("Bearer", v4.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task RateLimitedRequestsAreRetried()
    {
        var calls = 0;
        _server.Routes["/configuration"] = () =>
        {
            if (calls++ == 0)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
                return limited;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        };

        await Client().TestConnectionAsync();

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RepeatLookupsAreServedFromCache()
    {
        _server.Json("/search/tv", BreakingBadSearch);
        _server.Json("/tv/1396/season/2", BreakingBadSeason2);
        var matcher = Matcher();

        await Task.WhenAll(
            matcher.MatchAsync(Show("Breaking Bad", 2, 5), @"D:\x\a.mkv"),
            matcher.MatchAsync(Show("Breaking Bad", 2, 6), @"D:\x\b.mkv"));

        Assert.Equal(2, _server.Requests.Count);
    }
}
