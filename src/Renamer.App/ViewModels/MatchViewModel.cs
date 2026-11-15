using System.Collections.ObjectModel;
using System.Windows.Input;
using Renamer.Core;

namespace Renamer.App.ViewModels;

/// <summary>Backs the "Change match" window: search TMDB by hand and choose the right entry.</summary>
public sealed class MatchViewModel : ObservableObject
{
    private readonly MediaItem _item;
    private readonly IReadOnlyList<MediaItem> _sameShow;
    private readonly MediaMatcher _matcher;

    private bool _isTv;
    private string _query;
    private string _yearText;
    private string _seasonText;
    private string _episodeText;
    private string _episodeEndText;
    private bool _applyToSameShow = true;
    private TmdbCandidate? _selected;
    private bool _isSearching;
    private string? _searchMessage;

    public MatchViewModel(MediaItem item, IEnumerable<MediaItem> allItems, MediaMatcher matcher)
    {
        _item = item;
        _matcher = matcher;

        var parsed = item.Parsed;
        _isTv = parsed.Kind == MediaKind.Episode;
        _query = parsed.Title;
        _yearText = parsed.Year?.ToString() ?? "";
        _seasonText = (item.Info?.Season ?? parsed.Season)?.ToString() ?? "";
        _episodeText = (item.Info?.Episodes.FirstOrDefault() is > 0 and var first ? first : parsed.Episode)?.ToString() ?? "";
        _episodeEndText = (item.Info?.Episodes.Count > 1 ? item.Info.Episodes[^1] : parsed.EpisodeEnd)?.ToString() ?? "";

        // Other files read as the same show get this choice too, which is usually what's wanted
        // when a whole season was matched to the wrong one.
        var title = TitleMatch.Normalize(parsed.Title);
        _sameShow = title.Length == 0
            ? []
            : allItems.Where(other =>
                    other != item &&
                    other.Status is not (ItemStatus.Renamed or ItemStatus.Renaming) &&
                    other.Parsed.Kind == MediaKind.Episode &&
                    TitleMatch.Normalize(other.Parsed.Title) == title)
                .ToList();

        foreach (var candidate in item.Candidates)
        {
            Results.Add(candidate);
        }

        _selected = Results.FirstOrDefault();

        SearchCommand = new RelayCommand(Search, () => !IsSearching && !string.IsNullOrWhiteSpace(Query));

        if (Results.Count == 0 && !string.IsNullOrWhiteSpace(Query))
        {
            Search();
        }
    }

    public ICommand SearchCommand { get; }

    public ObservableCollection<TmdbCandidate> Results { get; } = [];

    public string FileName => _item.FileName;

    public string? CurrentProblem => _item.Message;

    public bool IsTv
    {
        get => _isTv;
        set
        {
            if (Set(ref _isTv, value))
            {
                OnPropertyChanged(nameof(IsMovie));
                OnPropertyChanged(nameof(ShowApplyToSameShow));
                Results.Clear();
                Selected = null;
                Search();
            }
        }
    }

    public bool IsMovie
    {
        get => !_isTv;
        set => IsTv = !value;
    }

    public string Query
    {
        get => _query;
        set
        {
            if (Set(ref _query, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string YearText
    {
        get => _yearText;
        set => Set(ref _yearText, value);
    }

    public string SeasonText
    {
        get => _seasonText;
        set
        {
            if (Set(ref _seasonText, value))
            {
                OnPropertyChanged(nameof(CanApply));
            }
        }
    }

    public string EpisodeText
    {
        get => _episodeText;
        set
        {
            if (Set(ref _episodeText, value))
            {
                OnPropertyChanged(nameof(CanApply));
            }
        }
    }

    public string EpisodeEndText
    {
        get => _episodeEndText;
        set => Set(ref _episodeEndText, value);
    }

    public bool ApplyToSameShow
    {
        get => _applyToSameShow;
        set => Set(ref _applyToSameShow, value);
    }

    public bool ShowApplyToSameShow => IsTv && _sameShow.Count > 0;

    public string ApplyToSameShowLabel =>
        _sameShow.Count == 1
            ? "Use this show for 1 other file read as the same show"
            : $"Use this show for {_sameShow.Count} other files read as the same show";

    public TmdbCandidate? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                OnPropertyChanged(nameof(CanApply));
            }
        }
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (Set(ref _isSearching, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string? SearchMessage
    {
        get => _searchMessage;
        private set => Set(ref _searchMessage, value);
    }

    public bool CanApply
    {
        get
        {
            if (Selected is null)
            {
                return false;
            }

            if (!IsTv)
            {
                return true;
            }

            var hasNumbers = int.TryParse(SeasonText, out var season) && season >= 0 && int.TryParse(EpisodeText, out var episode) && episode >= 0;
            return hasNumbers || HasFoundEpisodeAnotherWay;
        }
    }

    private bool HasFoundEpisodeAnotherWay => _item.Parsed.Absolute is not null || _item.Parsed.AirDate is not null;

    /// <summary>The files to update and how each should be read, once the choice is confirmed.</summary>
    public IReadOnlyList<(MediaItem Item, ParsedMedia Parsed)> BuildTargets()
    {
        var targets = new List<(MediaItem, ParsedMedia)>();
        var parsed = _item.Parsed;

        if (!IsTv)
        {
            targets.Add((_item, parsed with { Kind = MediaKind.Movie, Season = null, Episode = null, EpisodeEnd = null, Absolute = null, AirDate = null }));
            return targets;
        }

        if (int.TryParse(SeasonText, out var season) && int.TryParse(EpisodeText, out var episode))
        {
            parsed = parsed with
            {
                Kind = MediaKind.Episode,
                Season = season,
                Episode = episode,
                EpisodeEnd = int.TryParse(EpisodeEndText, out var end) && end > episode ? end : null,
                Absolute = null,
                AirDate = null,
            };
        }

        targets.Add((_item, parsed));

        if (ApplyToSameShow)
        {
            targets.AddRange(_sameShow.Select(other => (other, other.Parsed)));
        }

        return targets;
    }

    private async void Search()
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            return;
        }

        IsSearching = true;
        SearchMessage = null;
        try
        {
            var year = int.TryParse(YearText, out var parsedYear) ? parsedYear : (int?)null;
            var kind = IsTv ? MediaKind.Episode : MediaKind.Movie;
            var found = await _matcher.SearchAsync(kind, Query.Trim(), year);

            Results.Clear();
            foreach (var candidate in found.OrderByDescending(c => c.Popularity))
            {
                Results.Add(candidate);
            }

            Selected = Results.FirstOrDefault();
            if (Results.Count == 0)
            {
                SearchMessage = "Nothing found. Try a shorter title, or remove the year.";
            }
        }
        catch (TmdbException ex)
        {
            SearchMessage = ex.Message;
        }
        finally
        {
            IsSearching = false;
        }
    }
}
