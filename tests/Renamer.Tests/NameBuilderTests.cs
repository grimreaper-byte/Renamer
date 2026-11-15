using Renamer.Core;

namespace Renamer.Tests;

public class NameBuilderTests
{
    private static readonly MediaInfo Movie = new()
    {
        Kind = MediaKind.Movie,
        Title = "The Matrix",
        Year = 1999,
        TmdbId = 603,
    };

    private static readonly MediaInfo Episode = new()
    {
        Kind = MediaKind.Episode,
        Title = "Breaking Bad",
        Year = 2008,
        TmdbId = 1396,
        Season = 2,
        Episodes = [5],
        EpisodeTitle = "Breakage",
    };

    private static NameBuilder Builder(string preset = "Plex", Action<AppSettings>? configure = null)
    {
        var settings = new AppSettings { Templates = Defaults.Presets.Single(p => p.Name == preset).Templates.Clone() };
        configure?.Invoke(settings);
        return new NameBuilder(settings);
    }

    [Theory]
    [InlineData("Plex", "The Matrix (1999)", "The Matrix (1999)")]
    [InlineData("Kodi", "The Matrix (1999)", "The Matrix (1999)")]
    [InlineData("Plex + TMDB ID", "The Matrix (1999) {tmdb-603}", "The Matrix (1999) {tmdb-603}")]
    [InlineData("Reelhouse", "The Matrix (1999)", "The Matrix (1999)")]
    public void MoviePresets(string preset, string folder, string file)
    {
        var builder = Builder(preset);

        Assert.Equal(folder, builder.RenderFolder(Movie));
        Assert.Equal(file, builder.RenderFile(Movie));
    }

    [Theory]
    [InlineData("Plex", @"Breaking Bad (2008)\Season 02", "Breaking Bad (2008) - s02e05 - Breakage")]
    [InlineData("Plex + TMDB ID", @"Breaking Bad (2008) {tmdb-1396}\Season 02", "Breaking Bad (2008) - s02e05 - Breakage")]
    [InlineData("Kodi", @"Breaking Bad (2008)\Season 02", "Breaking Bad S02E05 - Breakage")]
    [InlineData("Reelhouse", @"Breaking Bad (2008)\Series 2", "Breaking Bad (2008) - 2x05 - Breakage")]
    public void EpisodePresets(string preset, string folder, string file)
    {
        var builder = Builder(preset);

        Assert.Equal(folder, builder.RenderFolder(Episode));
        Assert.Equal(file, builder.RenderFile(Episode));
    }

    [Theory]
    [InlineData("Plex", "Show (2020) - s01e01-e03 - A & B & C")]
    [InlineData("Kodi", "Show S01E01E02E03 - A & B & C")]
    [InlineData("Reelhouse", "Show (2020) - 1x01-03 - A & B & C")]
    public void MultiEpisodeFilesFollowEachApp(string preset, string expected)
    {
        var info = new MediaInfo
        {
            Kind = MediaKind.Episode,
            Title = "Show",
            Year = 2020,
            Season = 1,
            Episodes = [1, 2, 3],
            EpisodeTitle = "A & B & C",
        };

        Assert.Equal(expected, Builder(preset).RenderFile(info));
    }

    [Fact]
    public void MissingValuesLeaveNoDanglingPunctuation()
    {
        var info = new MediaInfo { Kind = MediaKind.Episode, Title = "Show", Season = 1, Episodes = [2] };

        Assert.Equal("Show - s01e02", Builder().RenderFile(info));
        Assert.Equal(@"Show\Season 01", Builder().RenderFolder(info));
    }

    [Theory]
    [InlineData("Star Trek: Discovery", "Star Trek - Discovery")]
    [InlineData("Face/Off", "Face-Off")]
    [InlineData("What If...?", "What If")]
    [InlineData("The \"Thing\"", "The 'Thing'")]
    [InlineData("A<B>C|D*E", "ABCDE")]
    [InlineData("AC\\DC", "AC-DC")]
    public void TitlesCannotBreakTheFileName(string title, string expected)
    {
        var settings = new Action<AppSettings>(s => s.Templates.MovieFile = "{title}");
        var name = Builder(configure: settings).RenderFile(Movie with { Title = title, Year = null });

        Assert.Equal(expected, name);
    }

    [Fact]
    public void ASlashInATitleDoesNotAddAFolder()
    {
        var folder = Builder().RenderFolder(Movie with { Title = "Face/Off", Year = 1997 });

        Assert.Equal("Face-Off (1997)", folder);
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul", "_nul")]
    public void ReservedWindowsNamesAreChanged(string title, string expected)
    {
        var builder = Builder(configure: s => s.Templates.MovieFile = "{title}");

        Assert.Equal(expected, builder.RenderFile(Movie with { Title = title }));
    }

    [Fact]
    public void LongNamesAreShortened()
    {
        var info = Episode with { EpisodeTitle = new string('x', 400) };

        Assert.True(Builder().RenderFile(info).Length <= 230);
    }

    [Fact]
    public void FinalNameRulesRunButCannotAddIllegalCharacters()
    {
        var builder = Builder(configure: s => s.Rules =
        [
            new RegexRule { Stage = RuleStage.FinalName, Find = "Matrix", Replace = "Mat?rix" },
        ]);

        Assert.Equal("The Matrix (1999)", builder.RenderFile(Movie));
    }

    [Fact]
    public void FinalNameRulesChangeTheName()
    {
        var builder = Builder(configure: s => s.Rules =
        [
            new RegexRule { Stage = RuleStage.FinalName, Find = @"^The (.+) \(", Replace = "$1, The (" },
        ]);

        Assert.Equal("Matrix, The (1999)", builder.RenderFile(Movie));
    }

    [Fact]
    public void QualityAndFormatTokensWork()
    {
        var builder = Builder(configure: s => s.Templates.MovieFile = "{title} [{resolution}] {year:0000} {air_date:yyyy.MM.dd}");
        var info = Movie with { Resolution = "1080p", AirDate = new DateOnly(1999, 3, 31) };

        Assert.Equal("The Matrix [1080p] 1999 1999.03.31", builder.RenderFile(info));
    }

    [Fact]
    public void UnknownTokensAreReported()
    {
        Assert.Equal(["bogus"], NameBuilder.FindUnknownTokens("{title} {bogus} {year:0000}"));
    }

    [Fact]
    public void RenameInPlaceKeepsTheFolderAndExtension()
    {
        var plan = Builder().Build(Episode, @"D:\DL\breaking.bad.205.MKV", out var error);

        Assert.Null(error);
        Assert.Equal(@"D:\DL\Breaking Bad (2008) - s02e05 - Breakage.MKV", plan!.DestinationPath);
        Assert.Equal("Breaking Bad (2008) - s02e05 - Breakage.MKV", plan.DisplayName);
    }

    [Fact]
    public void LibraryModeBuildsTheFullStructure()
    {
        var builder = Builder(configure: s =>
        {
            s.Destination = DestinationMode.MoveToLibrary;
            s.TvLibrary = @"E:\TV Shows";
        });

        var plan = builder.Build(Episode, @"D:\DL\x.mkv", out _);

        Assert.Equal(@"E:\TV Shows\Breaking Bad (2008)\Season 02\Breaking Bad (2008) - s02e05 - Breakage.mkv", plan!.DestinationPath);
    }

    [Fact]
    public void LibraryModeNeedsALibraryFolder()
    {
        var builder = Builder(configure: s => s.Destination = DestinationMode.MoveToLibrary);

        var plan = builder.Build(Movie, @"D:\DL\x.mkv", out var error);

        Assert.Null(plan);
        Assert.Contains("movie library", error);
    }

    [Fact]
    public void TemplatesCannotEscapeTheLibraryFolder()
    {
        var builder = Builder(configure: s =>
        {
            s.Destination = DestinationMode.MoveToLibrary;
            s.MovieLibrary = @"E:\Movies";
            s.Templates.MovieFolder = @"..\..\Elsewhere\{title}";
        });

        var plan = builder.Build(Movie, @"D:\DL\x.mkv", out _);

        Assert.StartsWith(@"E:\Movies\", plan!.DestinationPath);
    }
}
