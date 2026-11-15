using Renamer.Core;

namespace Renamer.Tests;

public class FilenameParserTests
{
    private readonly FilenameParser _parser = new(new AppSettings());

    [Theory]
    [InlineData(@"D:\x\Breaking.Bad.S02E05.1080p.BluRay.x264-GRP.mkv", "Breaking Bad", null, 2, 5, null)]
    [InlineData(@"D:\x\breaking bad s02e05.mkv", "breaking bad", null, 2, 5, null)]
    [InlineData(@"D:\x\The.Office.US.S03E07E08.720p.WEB-DL.mkv", "The Office US", null, 3, 7, 8)]
    [InlineData(@"D:\x\Show.S01E02-E03.mkv", "Show", null, 1, 2, 3)]
    [InlineData(@"D:\x\Show.S01E02-03.mkv", "Show", null, 1, 2, 3)]
    [InlineData(@"D:\x\Show.S01E02-720p.mkv", "Show", null, 1, 2, null)]
    [InlineData(@"D:\x\Show.S01.E02.mkv", "Show", null, 1, 2, null)]
    [InlineData(@"D:\x\Game of Thrones - 1x09 - Baelor.mp4", "Game of Thrones", null, 1, 9, null)]
    [InlineData(@"D:\x\Show.2x03x04.mkv", "Show", null, 2, 3, 4)]
    [InlineData(@"D:\x\Doctor.Who.2005.S01E01.mkv", "Doctor Who", 2005, 1, 1, null)]
    [InlineData(@"D:\x\Doctor Who (2005) - S01E01 - Rose.mkv", "Doctor Who", 2005, 1, 1, null)]
    [InlineData(@"D:\x\Marvels.Agents.of.S.H.I.E.L.D.S01E01.mkv", "Marvels Agents of S.H.I.E.L.D.", null, 1, 1, null)]
    [InlineData(@"D:\x\S.W.A.T.2017.S01E01.mkv", "S.W.A.T.", 2017, 1, 1, null)]
    [InlineData(@"D:\x\1923.S01E01.mkv", "1923", null, 1, 1, null)]
    [InlineData(@"D:\x\Show Season 2 Episode 7.mkv", "Show", null, 2, 7, null)]
    [InlineData(@"D:\x\www.Example.com - Show.Name.S01E02.mkv", "Show Name", null, 1, 2, null)]
    public void ReadsEpisodes(string path, string title, int? year, int season, int episode, int? end)
    {
        var parsed = _parser.Parse(path);

        Assert.Equal(MediaKind.Episode, parsed.Kind);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episode, parsed.Episode);
        Assert.Equal(end, parsed.EpisodeEnd);
    }

    [Theory]
    [InlineData(@"D:\x\The.Matrix.1999.1080p.BluRay.mkv", "The Matrix", 1999)]
    [InlineData(@"D:\x\Blade.Runner.2049.2017.2160p.mkv", "Blade Runner 2049", 2017)]
    [InlineData(@"D:\x\Blade.Runner.2049.1080p.mkv", "Blade Runner 2049", null)]
    [InlineData(@"D:\x\2001.A.Space.Odyssey.1968.mkv", "2001 A Space Odyssey", 1968)]
    [InlineData(@"D:\x\1917.2019.1080p.mkv", "1917", 2019)]
    [InlineData(@"D:\x\Some Movie (2010).avi", "Some Movie", 2010)]
    [InlineData(@"D:\x\Some Movie [2010] [1080p].avi", "Some Movie", 2010)]
    [InlineData(@"D:\x\Charlotte's Web.mkv", "Charlotte's Web", null)]
    [InlineData(@"D:\x\Inception.mkv", "Inception", null)]
    [InlineData(@"D:\x\Inception.1080p.BluRay.x264-GRP.mkv", "Inception", null)]
    [InlineData(@"D:\x\E.T.the.Extra-Terrestrial.1982.mkv", "E.T. the Extra-Terrestrial", 1982)]
    [InlineData(@"D:\x\Kung Fu Panda - 2 (2011).mkv", "Kung Fu Panda - 2", 2011)]
    public void ReadsMovies(string path, string title, int? year)
    {
        var parsed = _parser.Parse(path);

        Assert.Equal(MediaKind.Movie, parsed.Kind);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
    }

    [Theory]
    [InlineData(@"D:\x\[SubsPlease] Frieren - 12 (1080p) [ABCD1234].mkv", "Frieren", 12)]
    [InlineData(@"D:\x\[Group] Long Show Name - 103v2 [720p].mkv", "Long Show Name", 103)]
    public void ReadsAbsoluteNumbers(string path, string title, int absolute)
    {
        var parsed = _parser.Parse(path);

        Assert.Equal(MediaKind.Episode, parsed.Kind);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(absolute, parsed.Absolute);
        Assert.Null(parsed.Season);
    }

    [Fact]
    public void ReadsAirDates()
    {
        var parsed = _parser.Parse(@"D:\x\The.Daily.Show.2024.03.15.Guest.Name.1080p.mkv");

        Assert.Equal(MediaKind.Episode, parsed.Kind);
        Assert.Equal("The Daily Show", parsed.Title);
        Assert.Equal(new DateOnly(2024, 3, 15), parsed.AirDate);
    }

    [Fact]
    public void AnImpossibleDateIsNotAnAirDate()
    {
        var parsed = _parser.Parse(@"D:\x\Some.Movie.2024.02.31.mkv");

        Assert.Null(parsed.AirDate);
    }

    [Theory]
    [InlineData(@"D:\TV\Breaking Bad (2008)\Season 2\05 - Breakage.mkv", "Breaking Bad", 2008, 2, 5)]
    [InlineData(@"D:\TV\Breaking Bad\Season 02\S02E05.mkv", "Breaking Bad", null, 2, 5)]
    [InlineData(@"D:\TV\Breaking Bad\Season 02\E05.mkv", "Breaking Bad", null, 2, 5)]
    [InlineData(@"D:\TV\Breaking Bad\Season 02\Episode 5.mkv", "Breaking Bad", null, 2, 5)]
    [InlineData(@"D:\TV\Breaking Bad\Specials\03 - Extra.mkv", "Breaking Bad", null, 0, 3)]
    [InlineData(@"D:\DL\Show.Name.S02.1080p.WEB\e05.mkv", "Show Name", null, 2, 5)]
    [InlineData(@"D:\DL\Some.Show.S01E03.1080p.WEB\abc123.mkv", "Some Show", null, 1, 3)]
    [InlineData(@"D:\DL\Some.Show\Episode 4.mkv", "Some Show", null, 1, 4)]
    public void UsesFoldersForEpisodes(string path, string title, int? year, int season, int episode)
    {
        var parsed = _parser.Parse(path);

        Assert.Equal(MediaKind.Episode, parsed.Kind);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episode, parsed.Episode);
    }

    [Theory]
    [InlineData(@"D:\DL\Movie.Name.2019.1080p.BluRay\abc123.mkv", "Movie Name", 2019)]
    [InlineData(@"D:\Movies\Movie Name (2019)\abc123.mkv", "Movie Name", 2019)]
    [InlineData(@"D:\Movies\Movie Name 2019\movie name.mkv", "Movie Name", 2019)]
    public void UsesFoldersForMovies(string path, string title, int year)
    {
        var parsed = _parser.Parse(path);

        Assert.Equal(MediaKind.Movie, parsed.Kind);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
    }

    [Theory]
    [InlineData(@"D:\Downloads\Inception.mkv", "Inception")]
    [InlineData(@"D:\Best Films 2019\Inception.mkv", "Inception")]
    [InlineData(@"D:\Star Wars Collection\Star.Wars.Episode.4.A.New.Hope.mkv", "Star Wars Episode 4 A New Hope")]
    public void IgnoresFoldersThatDoNotDescribeTheFile(string path, string title)
    {
        var parsed = _parser.Parse(path);

        Assert.Equal(MediaKind.Movie, parsed.Kind);
        Assert.Equal(title, parsed.Title);
        Assert.Null(parsed.Year);
    }

    [Fact]
    public void ReadsQualityTags()
    {
        var parsed = _parser.Parse(@"D:\x\Show_Name_S01E02_1080p_WEB-DL_x265.mkv");

        Assert.Equal("1080p", parsed.Resolution);
        Assert.Equal("WEB-DL", parsed.Source);
        Assert.Equal("x265", parsed.Codec);
    }

    [Fact]
    public void ResolutionIsNotMistakenForAnEpisode()
    {
        var parsed = _parser.Parse(@"D:\x\Some.Movie.1920x1080.mkv");

        Assert.Equal(MediaKind.Movie, parsed.Kind);
    }

    [Fact]
    public void CompactNumbersAreOffByDefaultButCanBeEnabled()
    {
        const string Path = @"D:\x\Fahrenheit.451.mkv";
        Assert.Equal(MediaKind.Movie, _parser.Parse(Path).Kind);

        var settings = new AppSettings();
        settings.Patterns.Single(p => p.Name.StartsWith("Compact")).Enabled = true;
        var parsed = new FilenameParser(settings).Parse(Path);

        Assert.Equal(MediaKind.Episode, parsed.Kind);
        Assert.Equal(4, parsed.Season);
        Assert.Equal(51, parsed.Episode);
    }

    [Fact]
    public void CustomPatternsAndRulesAreUsed()
    {
        var settings = new AppSettings
        {
            Patterns =
            [
                new ParsePattern
                {
                    Name = "Custom",
                    Kind = MediaKind.Episode,
                    Regex = @"^(?<title>.+) Folge (?<episode>\d+) Staffel (?<season>\d+)$",
                },
            ],
            Rules =
            [
                new RegexRule { Stage = RuleStage.BeforeMatching, Find = @"^JUNK ", Replace = "" },
            ],
        };

        var parsed = new FilenameParser(settings).Parse(@"D:\x\JUNK Mein Lied Folge 3 Staffel 2.mkv");

        Assert.Equal("Mein Lied", parsed.Title);
        Assert.Equal(2, parsed.Season);
        Assert.Equal(3, parsed.Episode);
        Assert.Equal("Custom", parsed.Pattern);
    }

    [Fact]
    public void BrokenAndRunawayPatternsAreSkipped()
    {
        var settings = new AppSettings
        {
            Patterns =
            [
                new ParsePattern { Name = "Broken", Kind = MediaKind.Movie, Regex = "(" },
                new ParsePattern { Name = "Runaway", Kind = MediaKind.Movie, Regex = @"^(?<title>(a+)+)$" },
                new ParsePattern { Name = "Good", Kind = MediaKind.Movie, Regex = @"^(?<title>.+)$" },
            ],
            Rules = [],
        };

        var parsed = new FilenameParser(settings).Parse(@"D:\x\" + new string('a', 40) + "!.mkv");

        Assert.Equal("Good", parsed.Pattern);
    }
}
