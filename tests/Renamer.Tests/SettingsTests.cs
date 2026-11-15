using Renamer.Core;

namespace Renamer.Tests;

public class SettingsTests
{
    [Fact]
    public void TheApiKeyIsNeverWrittenAsPlainText()
    {
        var settings = new AppSettings { ApiKey = "super-secret" };

        Assert.DoesNotContain("super-secret", settings.ToJson());
    }

    [Fact]
    public void CloneKeepsTheKeyAndIsIndependent()
    {
        var settings = new AppSettings { ApiKey = "k", Language = "de-DE" };
        settings.Patterns[0].Name = "Changed";

        var copy = settings.Clone();
        copy.Patterns[0].Name = "Changed again";

        Assert.Equal("k", copy.ApiKey);
        Assert.Equal("de-DE", copy.Language);
        Assert.Equal("Changed", settings.Patterns[0].Name);
    }

    [Fact]
    public void JsonRoundTripKeepsEverything()
    {
        var settings = new AppSettings
        {
            Destination = DestinationMode.MoveToLibrary,
            MovieLibrary = @"E:\Movies",
            Theme = AppTheme.Light,
            Rules = [new RegexRule { Stage = RuleStage.FinalName, Find = "a", Replace = "b" }],
        };

        var loaded = AppSettings.FromJson(settings.ToJson());

        Assert.Equal(DestinationMode.MoveToLibrary, loaded.Destination);
        Assert.Equal(AppTheme.Light, loaded.Theme);
        Assert.Equal(@"E:\Movies", loaded.MovieLibrary);
        Assert.Equal(RuleStage.FinalName, Assert.Single(loaded.Rules).Stage);
        Assert.Equal(settings.Patterns.Count, loaded.Patterns.Count);
    }

    [Fact]
    public void FieldsMissingFromAnOlderFileGetDefaults()
    {
        var loaded = AppSettings.FromJson("""{"Language":"fr-FR"}""");

        Assert.Equal("fr-FR", loaded.Language);
        Assert.NotEmpty(loaded.Patterns);
        Assert.Equal("Reelhouse", loaded.Preset);
        Assert.Equal(AppTheme.System, loaded.Theme);
    }

    [Fact]
    public void ReelhouseIsFirstInTheListAndTheDefault()
    {
        var settings = new AppSettings();

        Assert.Equal("Reelhouse", Defaults.Presets[0].Name);
        Assert.Equal("Reelhouse", settings.Preset);
        Assert.Equal("Reelhouse", Defaults.PresetNameFor(settings.Templates));
    }

    [Fact]
    public void AllDefaultRegexesCompile()
    {
        foreach (var pattern in Defaults.Patterns())
        {
            Assert.True(RegexTools.TryCreate(pattern.Regex, out var error) is not null, $"{pattern.Name}: {error}");
        }

        foreach (var rule in Defaults.Rules())
        {
            Assert.True(RegexTools.TryCreate(rule.Find, out var error) is not null, $"{rule.Note}: {error}");
        }

        Assert.NotNull(RegexTools.TryCreate(Defaults.IgnorePattern, out _));
    }

    [Fact]
    public void AllPresetTemplatesUseKnownTokens()
    {
        foreach (var preset in Defaults.Presets)
        {
            var t = preset.Templates;
            Assert.Empty(NameBuilder.FindUnknownTokens(t.MovieFolder + t.MovieFile + t.TvFolder + t.TvFile));
        }
    }

    [Fact]
    public void TemplatesSavedAsCustomAreRecognisedAsTheMatchingPreset()
    {
        var saved = Defaults.Presets.Single(p => p.Name == "Reelhouse").Templates.Clone();

        Assert.Equal("Reelhouse", Defaults.PresetNameFor(saved));

        saved.TvFolder = "{show}/Series {season}";
        Assert.Equal(Defaults.CustomPreset, Defaults.PresetNameFor(saved));
    }

    [Fact]
    public void TheFirstReelhouseTemplatesMoveToTheCurrentOnes()
    {
        var saved = new NamingTemplates
        {
            MovieFolder = "{title} ({year})",
            MovieFile = "{title} ({year})",
            TvFolder = "{show} ({year})/Series {season}",
            TvFile = "{show} ({year}) - {season}x{episode:00} - {episode_title}",
        };

        var upgraded = Defaults.Upgrade(saved);

        Assert.Equal("{show} ({year}) - {season}x{episode:00}{episode_to} - {episode_title}", upgraded.TvFile);
        Assert.Equal("Reelhouse", Defaults.PresetNameFor(upgraded));
    }

    [Fact]
    public void TemplatesThatWereNeverAPresetAreLeftAlone()
    {
        var saved = new NamingTemplates { MovieFolder = "a", MovieFile = "b", TvFolder = "c", TvFile = "d" };

        Assert.Same(saved, Defaults.Upgrade(saved));
    }

    [Theory]
    [InlineData("Marvel's Agents", "Marvels Agents", 1.0)]
    [InlineData("The Matrix", "Matrix", 1.0)]
    [InlineData("Amélie", "Amelie", 1.0)]
    [InlineData("Fast & Furious", "Fast and Furious", 1.0)]
    [InlineData("S.W.A.T.", "SWAT", 1.0)]
    public void TitlesThatMeanTheSameCompareEqual(string a, string b, double expected)
    {
        Assert.Equal(expected, TitleMatch.Similarity(a, b));
    }

    [Fact]
    public void DifferentTitlesScoreLow()
    {
        Assert.True(TitleMatch.Similarity("Inception", "Interstellar") < 0.5);
    }
}
