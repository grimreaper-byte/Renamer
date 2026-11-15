using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Renamer.Core;

namespace Renamer.App.ViewModels;

public sealed class KindOption(MediaKind value, string label)
{
    public MediaKind Value { get; } = value;

    public string Label { get; } = label;
}

public sealed class StageOption(RuleStage value, string label)
{
    public RuleStage Value { get; } = value;

    public string Label { get; } = label;
}

public sealed record TokenInfo(string Token, string Meaning);

public sealed class TesterLine(string label, string value)
{
    public string Label { get; } = label;

    public string Value { get; } = value;
}

public sealed class PatternRow : ObservableObject
{
    private bool _enabled;
    private string _name;
    private MediaKind _kind;
    private string _regex;
    private string? _error;

    public PatternRow(ParsePattern pattern)
    {
        _enabled = pattern.Enabled;
        _name = pattern.Name;
        _kind = pattern.Kind;
        _regex = pattern.Regex;
        Validate();
    }

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public MediaKind Kind
    {
        get => _kind;
        set => Set(ref _kind, value);
    }

    public string Regex
    {
        get => _regex;
        set
        {
            if (Set(ref _regex, value))
            {
                Validate();
            }
        }
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public ParsePattern ToPattern() => new() { Enabled = Enabled, Name = Name, Kind = Kind, Regex = Regex };

    private void Validate()
    {
        RegexTools.TryCreate(_regex, out var error);
        Error = error;
    }
}

public sealed class RuleRow : ObservableObject
{
    private bool _enabled;
    private RuleStage _stage;
    private string _find;
    private string _replace;
    private string _note;
    private string? _error;

    public RuleRow(RegexRule rule)
    {
        _enabled = rule.Enabled;
        _stage = rule.Stage;
        _find = rule.Find;
        _replace = rule.Replace;
        _note = rule.Note;
        Validate();
    }

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public RuleStage Stage
    {
        get => _stage;
        set => Set(ref _stage, value);
    }

    public string Find
    {
        get => _find;
        set
        {
            if (Set(ref _find, value))
            {
                Validate();
            }
        }
    }

    public string Replace
    {
        get => _replace;
        set => Set(ref _replace, value);
    }

    public string Note
    {
        get => _note;
        set => Set(ref _note, value);
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public RegexRule ToRule() => new() { Enabled = Enabled, Stage = Stage, Find = Find, Replace = Replace, Note = Note };

    private void Validate()
    {
        RegexTools.TryCreate(_find, out var error);
        Error = error;
    }
}

/// <summary>A working copy of the settings. Nothing takes effect until Save.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private static readonly MediaInfo SampleMovie = new()
    {
        Kind = MediaKind.Movie,
        Title = "The Matrix",
        Year = 1999,
        TmdbId = 603,
        Resolution = "1080p",
        Source = "BluRay",
        Codec = "x265",
    };

    private static readonly MediaInfo SampleEpisode = new()
    {
        Kind = MediaKind.Episode,
        Title = "Breaking Bad",
        Year = 2008,
        TmdbId = 1396,
        Season = 2,
        Episodes = [5],
        EpisodeTitle = "Breakage",
        AirDate = new DateOnly(2009, 4, 5),
        Resolution = "1080p",
        Source = "BluRay",
        Codec = "x265",
    };

    private static readonly MediaInfo SampleMultiEpisode = SampleEpisode with
    {
        Episodes = [5, 6],
        EpisodeTitle = "Breakage & Peekaboo",
    };

    private readonly AppSettings _source;

    private string _apiKey;
    private string _language;
    private bool _autoMatch;
    private string? _testResult;
    private bool _testFailed;
    private bool _isTesting;
    private string _preset;
    private string _movieFolder;
    private string _movieFile;
    private string _tvFolder;
    private string _tvFile;
    private bool _moveToLibrary;
    private string _movieLibrary;
    private string _tvLibrary;
    private string _videoExtensions;
    private bool _renameSidecars;
    private string _sidecarExtensions;
    private string _ignorePattern;
    private PatternRow? _selectedPattern;
    private RuleRow? _selectedRule;
    private string _testerInput = "Breaking.Bad.S02E05.1080p.BluRay.x264-GRP.mkv";
    private bool _isApplyingPreset;
    private AppTheme _theme;

    public SettingsViewModel(AppSettings settings)
    {
        _source = settings;
        _apiKey = settings.ApiKey;
        _language = settings.Language;
        _autoMatch = settings.AutoMatch;
        _theme = settings.Theme;
        _preset = settings.Preset;
        _movieFolder = settings.Templates.MovieFolder;
        _movieFile = settings.Templates.MovieFile;
        _tvFolder = settings.Templates.TvFolder;
        _tvFile = settings.Templates.TvFile;
        _moveToLibrary = settings.Destination == DestinationMode.MoveToLibrary;
        _movieLibrary = settings.MovieLibrary;
        _tvLibrary = settings.TvLibrary;
        _videoExtensions = settings.VideoExtensions;
        _renameSidecars = settings.RenameSidecars;
        _sidecarExtensions = settings.SidecarExtensions;
        _ignorePattern = settings.IgnorePattern;

        Patterns = new ObservableCollection<PatternRow>(settings.Patterns.Select(p => new PatternRow(p)));
        Rules = new ObservableCollection<RuleRow>(settings.Rules.Select(r => new RuleRow(r)));

        Patterns.CollectionChanged += (_, e) => OnRowsChanged(e.NewItems, e.OldItems);
        Rules.CollectionChanged += (_, e) => OnRowsChanged(e.NewItems, e.OldItems);
        foreach (var row in Patterns.Cast<INotifyPropertyChanged>().Concat(Rules))
        {
            row.PropertyChanged += OnRowChanged;
        }

        TestCommand = new RelayCommand(() => TestConnection(), () => !IsTesting && !string.IsNullOrWhiteSpace(ApiKey));

        AddPatternCommand = new RelayCommand(AddPattern);
        RemovePatternCommand = new RelayCommand(() => Patterns.Remove(SelectedPattern!), () => SelectedPattern is not null);
        MovePatternUpCommand = new RelayCommand(() => Move(Patterns, SelectedPattern!, -1), () => CanMove(Patterns, SelectedPattern, -1));
        MovePatternDownCommand = new RelayCommand(() => Move(Patterns, SelectedPattern!, 1), () => CanMove(Patterns, SelectedPattern, 1));
        ResetPatternsCommand = new RelayCommand(ResetPatterns);

        AddRuleCommand = new RelayCommand(AddRule);
        RemoveRuleCommand = new RelayCommand(() => Rules.Remove(SelectedRule!), () => SelectedRule is not null);
        MoveRuleUpCommand = new RelayCommand(() => Move(Rules, SelectedRule!, -1), () => CanMove(Rules, SelectedRule, -1));
        MoveRuleDownCommand = new RelayCommand(() => Move(Rules, SelectedRule!, 1), () => CanMove(Rules, SelectedRule, 1));
        ResetRulesCommand = new RelayCommand(ResetRules);
    }

    public ICommand TestCommand { get; }

    public ICommand AddPatternCommand { get; }

    public ICommand RemovePatternCommand { get; }

    public ICommand MovePatternUpCommand { get; }

    public ICommand MovePatternDownCommand { get; }

    public ICommand ResetPatternsCommand { get; }

    public ICommand AddRuleCommand { get; }

    public ICommand RemoveRuleCommand { get; }

    public ICommand MoveRuleUpCommand { get; }

    public ICommand MoveRuleDownCommand { get; }

    public ICommand ResetRulesCommand { get; }

    public IReadOnlyList<string> PresetNames { get; } =
        [.. Defaults.Presets.Select(p => p.Name), Defaults.CustomPreset];

    public IReadOnlyList<string> Languages { get; } =
        ["en-US", "en-GB", "de-DE", "fr-FR", "es-ES", "it-IT", "nl-NL", "pt-BR", "ru-RU", "ja-JP", "ko-KR", "zh-CN"];

    public IReadOnlyList<KindOption> KindOptions { get; } =
        [new(MediaKind.Episode, "TV"), new(MediaKind.Movie, "Movie")];

    public IReadOnlyList<StageOption> StageOptions { get; } =
        [new(RuleStage.BeforeMatching, "Before matching"), new(RuleStage.FinalName, "Final name")];

    public IReadOnlyList<TokenInfo> Tokens { get; } =
    [
        new("{title}", "The movie title, or the show name for an episode"),
        new("{show}", "The same as {title}"),
        new("{year}", "Release year, or the year the show first aired"),
        new("{season}", "Season number"),
        new("{episode}", "Episode number, the first one if the file holds several"),
        new("{episode_end}", "The last episode number in the file"),
        new("{episode_range}", "-e06 on a multi-episode file, which is how Plex writes it. Empty otherwise."),
        new("{episode_list}", "E06 on a multi-episode file, which is how Kodi writes it. Empty otherwise."),
        new("{episode_to}", "-06 on a multi-episode file, as in 2x05-06. Empty otherwise."),
        new("{episode_title}", "The episode's name on TMDB"),
        new("{air_date}", "The date the episode first aired"),
        new("{tmdb_id}", "The title's TMDB number"),
        new("{resolution}", "From the file name, such as 1080p"),
        new("{source}", "From the file name, such as BluRay"),
        new("{codec}", "From the file name, such as x265"),
        new("{original_name}", "The file's current name"),
    ];

    public string TokenHelp { get; } =
        "Add a format after a colon: {season:00} gives 02, and {air_date:yyyy.MM.dd} gives 2009.04.05. " +
        "Use / in a folder template to make subfolders. Empty values are removed along with their brackets.";

    public string PatternHelp { get; } =
        "Tried top to bottom, and the first match wins. Named groups the app reads: title, year, season, " +
        "episode, episode_end, absolute, air_year, air_month, air_day. Matching ignores case. " +
        "Compact numbers are off because they misread titles like Fahrenheit 451.";

    public string RuleHelp { get; } =
        "\"Before matching\" rules clean the file name before the patterns read it. \"Final name\" rules " +
        "change the new name after the template fills it in, and $1 in Replace inserts a captured group. " +
        "Characters Windows does not allow are always removed afterwards.";

    public ObservableCollection<PatternRow> Patterns { get; }

    public ObservableCollection<RuleRow> Rules { get; }

    public bool IsSystemTheme
    {
        get => _theme == AppTheme.System;
        set => SelectTheme(AppTheme.System, value);
    }

    public bool IsLightTheme
    {
        get => _theme == AppTheme.Light;
        set => SelectTheme(AppTheme.Light, value);
    }

    public bool IsDarkTheme
    {
        get => _theme == AppTheme.Dark;
        set => SelectTheme(AppTheme.Dark, value);
    }

    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (Set(ref _apiKey, value))
            {
                TestResult = null;
            }
        }
    }

    public string Language
    {
        get => _language;
        set => Set(ref _language, value);
    }

    public bool AutoMatch
    {
        get => _autoMatch;
        set => Set(ref _autoMatch, value);
    }

    public string? TestResult
    {
        get => _testResult;
        private set => Set(ref _testResult, value);
    }

    public bool TestFailed
    {
        get => _testFailed;
        private set => Set(ref _testFailed, value);
    }

    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (Set(ref _isTesting, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string Preset
    {
        get => _preset;
        set
        {
            if (!Set(ref _preset, value))
            {
                return;
            }

            if (Defaults.Presets.FirstOrDefault(p => p.Name == value) is { } preset)
            {
                _isApplyingPreset = true;
                MovieFolder = preset.Templates.MovieFolder;
                MovieFile = preset.Templates.MovieFile;
                TvFolder = preset.Templates.TvFolder;
                TvFile = preset.Templates.TvFile;
                _isApplyingPreset = false;
                OnTemplateChanged();
            }
        }
    }

    public string MovieFolder
    {
        get => _movieFolder;
        set
        {
            if (Set(ref _movieFolder, value))
            {
                OnTemplateChanged();
            }
        }
    }

    public string MovieFile
    {
        get => _movieFile;
        set
        {
            if (Set(ref _movieFile, value))
            {
                OnTemplateChanged();
            }
        }
    }

    public string TvFolder
    {
        get => _tvFolder;
        set
        {
            if (Set(ref _tvFolder, value))
            {
                OnTemplateChanged();
            }
        }
    }

    public string TvFile
    {
        get => _tvFile;
        set
        {
            if (Set(ref _tvFile, value))
            {
                OnTemplateChanged();
            }
        }
    }

    public bool MoveToLibrary
    {
        get => _moveToLibrary;
        set
        {
            if (Set(ref _moveToLibrary, value))
            {
                OnPropertyChanged(nameof(RenameInPlace));
                OnPropertyChanged(nameof(PreviewMovie));
                OnPropertyChanged(nameof(PreviewEpisode));
                OnPropertyChanged(nameof(PreviewMultiEpisode));
                OnPropertyChanged(nameof(ValidationError));
            }
        }
    }

    public bool RenameInPlace
    {
        get => !_moveToLibrary;
        set => MoveToLibrary = !value;
    }

    public string MovieLibrary
    {
        get => _movieLibrary;
        set
        {
            if (Set(ref _movieLibrary, value))
            {
                OnPropertyChanged(nameof(PreviewMovie));
            }
        }
    }

    public string TvLibrary
    {
        get => _tvLibrary;
        set
        {
            if (Set(ref _tvLibrary, value))
            {
                OnPropertyChanged(nameof(PreviewEpisode));
                OnPropertyChanged(nameof(PreviewMultiEpisode));
            }
        }
    }

    public string PreviewMovie => Preview(SampleMovie);

    public string PreviewEpisode => Preview(SampleEpisode);

    public string PreviewMultiEpisode => Preview(SampleMultiEpisode);

    public string VideoExtensions
    {
        get => _videoExtensions;
        set => Set(ref _videoExtensions, value);
    }

    public bool RenameSidecars
    {
        get => _renameSidecars;
        set => Set(ref _renameSidecars, value);
    }

    public string SidecarExtensions
    {
        get => _sidecarExtensions;
        set => Set(ref _sidecarExtensions, value);
    }

    public string IgnorePattern
    {
        get => _ignorePattern;
        set
        {
            if (Set(ref _ignorePattern, value))
            {
                OnPropertyChanged(nameof(IgnoreError));
                OnPropertyChanged(nameof(ValidationError));
            }
        }
    }

    public string? IgnoreError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_ignorePattern))
            {
                return null;
            }

            RegexTools.TryCreate(_ignorePattern, out var error);
            return error;
        }
    }

    public PatternRow? SelectedPattern
    {
        get => _selectedPattern;
        set
        {
            if (Set(ref _selectedPattern, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public RuleRow? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (Set(ref _selectedRule, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string TesterInput
    {
        get => _testerInput;
        set
        {
            if (Set(ref _testerInput, value))
            {
                OnPropertyChanged(nameof(TesterLines));
            }
        }
    }

    /// <summary>What the patterns and rules currently on screen read from the sample name.</summary>
    public IReadOnlyList<TesterLine> TesterLines
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TesterInput))
            {
                return [];
            }

            var draft = BuildSettings();
            var parser = new FilenameParser(draft);
            var parsed = parser.Parse(TesterInput.Trim());

            var lines = new List<TesterLine>
            {
                new("Pattern", parsed.Pattern ?? "None matched"),
                new("Type", parsed.Kind == MediaKind.Episode ? "TV episode" : "Movie"),
                new("Title", parsed.Title.Length == 0 ? "(none, taken from folders)" : parsed.Title),
            };

            if (parsed.Year is not null)
            {
                lines.Add(new("Year", parsed.Year.Value.ToString()));
            }

            if (parsed.Kind == MediaKind.Episode)
            {
                if (parsed.Season is not null)
                {
                    lines.Add(new("Season", parsed.Season.Value.ToString()));
                }

                if (parsed.Episode is not null)
                {
                    var episodes = parsed.EpisodeEnd is null ? $"{parsed.Episode}" : $"{parsed.Episode} to {parsed.EpisodeEnd}";
                    lines.Add(new("Episode", episodes));
                }

                if (parsed.Absolute is not null)
                {
                    lines.Add(new("Absolute number", parsed.Absolute.Value.ToString()));
                }

                if (parsed.AirDate is not null)
                {
                    lines.Add(new("Air date", parsed.AirDate.Value.ToString("yyyy-MM-dd")));
                }
            }

            var tags = string.Join(", ", new[] { parsed.Resolution, parsed.Source, parsed.Codec }.Where(t => t is not null));
            if (tags.Length > 0)
            {
                lines.Add(new("Quality tags", tags));
            }

            return lines;
        }
    }

    /// <summary>The first problem that should stop Save, or null when everything is valid.</summary>
    public string? ValidationError
    {
        get
        {
            var pattern = Patterns.FirstOrDefault(p => p.Error is not null);
            if (pattern is not null)
            {
                return $"Filename regex \"{pattern.Name}\": {pattern.Error}";
            }

            var rule = Rules.FirstOrDefault(r => r.Error is not null);
            if (rule is not null)
            {
                return $"Clean-up rule \"{rule.Find}\": {rule.Error}";
            }

            if (IgnoreError is not null)
            {
                return $"Skip pattern: {IgnoreError}";
            }

            var unknown = NameBuilder.FindUnknownTokens(MovieFolder + MovieFile + TvFolder + TvFile).FirstOrDefault();
            if (unknown is not null)
            {
                return $"Naming template: there is no {{{unknown}}} token.";
            }

            if (string.IsNullOrWhiteSpace(MovieFile) || string.IsNullOrWhiteSpace(TvFile))
            {
                return "Naming template: the file name templates cannot be empty.";
            }

            return null;
        }
    }

    public AppSettings BuildSettings()
    {
        var result = _source.Clone();
        result.ApiKey = ApiKey.Trim();
        result.Language = string.IsNullOrWhiteSpace(Language) ? "en-US" : Language.Trim();
        result.AutoMatch = AutoMatch;
        result.Theme = _theme;
        result.Preset = Preset;
        result.Templates = new NamingTemplates
        {
            MovieFolder = MovieFolder,
            MovieFile = MovieFile,
            TvFolder = TvFolder,
            TvFile = TvFile,
        };
        result.Destination = MoveToLibrary ? DestinationMode.MoveToLibrary : DestinationMode.RenameInPlace;
        result.MovieLibrary = MovieLibrary.Trim();
        result.TvLibrary = TvLibrary.Trim();
        result.VideoExtensions = VideoExtensions;
        result.RenameSidecars = RenameSidecars;
        result.SidecarExtensions = SidecarExtensions;
        result.IgnorePattern = IgnorePattern;
        result.Patterns = Patterns.Select(p => p.ToPattern()).ToList();
        result.Rules = Rules.Select(r => r.ToRule()).ToList();
        return result;
    }

    private void SelectTheme(AppTheme theme, bool isChosen)
    {
        if (isChosen)
        {
            _theme = theme;
        }
    }

    private async void TestConnection()
    {
        IsTesting = true;
        TestFailed = false;
        TestResult = "Checking…";
        try
        {
            await MainViewModel.CreateClient(BuildSettings()).TestConnectionAsync();
            TestResult = "Connected. TMDB accepted this key.";
        }
        catch (TmdbAuthException)
        {
            TestFailed = true;
            TestResult = "TMDB rejected this key. Check that it was copied in full.";
        }
        catch (TmdbException ex)
        {
            TestFailed = true;
            TestResult = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            TestFailed = true;
            TestResult = "Could not reach TMDB. Check your internet connection.";
        }
        finally
        {
            IsTesting = false;
        }
    }

    private void OnTemplateChanged()
    {
        if (_isApplyingPreset)
        {
            return;
        }

        var current = new NamingTemplates { MovieFolder = MovieFolder, MovieFile = MovieFile, TvFolder = TvFolder, TvFile = TvFile };
        var preset = Defaults.PresetNameFor(current);

        // Sets the field directly, because the Preset property would overwrite the templates.
        Set(ref _preset, preset, nameof(Preset));

        OnPropertyChanged(nameof(PreviewMovie));
        OnPropertyChanged(nameof(PreviewEpisode));
        OnPropertyChanged(nameof(PreviewMultiEpisode));
        OnPropertyChanged(nameof(ValidationError));
    }

    private string Preview(MediaInfo sample)
    {
        var draft = _source.Clone();
        draft.Templates = new NamingTemplates { MovieFolder = MovieFolder, MovieFile = MovieFile, TvFolder = TvFolder, TvFile = TvFile };
        draft.Rules = Rules.Select(r => r.ToRule()).ToList();
        var builder = new NameBuilder(draft);

        var file = builder.RenderFile(sample) + ".mkv";
        if (!MoveToLibrary)
        {
            return file;
        }

        var root = sample.Kind == MediaKind.Movie ? MovieLibrary : TvLibrary;
        var label = string.IsNullOrWhiteSpace(root) ? "(library folder)" : root.Trim();
        return Path.Combine(label, builder.RenderFolder(sample), file);
    }

    private void OnRowsChanged(System.Collections.IList? added, System.Collections.IList? removed)
    {
        foreach (INotifyPropertyChanged row in added ?? Array.Empty<object>())
        {
            row.PropertyChanged += OnRowChanged;
        }

        foreach (INotifyPropertyChanged row in removed ?? Array.Empty<object>())
        {
            row.PropertyChanged -= OnRowChanged;
        }

        OnRowChanged(null, new PropertyChangedEventArgs(null));
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(TesterLines));
        OnPropertyChanged(nameof(ValidationError));
        OnPropertyChanged(nameof(PreviewMovie));
        OnPropertyChanged(nameof(PreviewEpisode));
        OnPropertyChanged(nameof(PreviewMultiEpisode));
    }

    private void AddPattern()
    {
        var row = new PatternRow(new ParsePattern
        {
            Name = "New pattern",
            Kind = MediaKind.Episode,
            Regex = @"^(?<title>.+?)[\s._-]+(?<season>\d+)x(?<episode>\d+)",
        });

        // Goes first so it is tried before the built-in patterns.
        Patterns.Insert(0, row);
        SelectedPattern = row;
    }

    private void ResetPatterns()
    {
        Patterns.Clear();
        foreach (var pattern in Defaults.Patterns())
        {
            Patterns.Add(new PatternRow(pattern));
        }
    }

    private void AddRule()
    {
        var row = new RuleRow(new RegexRule { Stage = RuleStage.BeforeMatching, Find = @"\bExample\b", Replace = "", Note = "New rule" });
        Rules.Add(row);
        SelectedRule = row;
    }

    private void ResetRules()
    {
        Rules.Clear();
        foreach (var rule in Defaults.Rules())
        {
            Rules.Add(new RuleRow(rule));
        }
    }

    private static bool CanMove<T>(ObservableCollection<T> rows, T? row, int offset)
        where T : class
    {
        if (row is null)
        {
            return false;
        }

        var target = rows.IndexOf(row) + offset;
        return target >= 0 && target < rows.Count;
    }

    private static void Move<T>(ObservableCollection<T> rows, T row, int offset)
    {
        var index = rows.IndexOf(row);
        rows.Move(index, index + offset);
    }
}
