using System.IO;
using Renamer.Core;

namespace Renamer.App.ViewModels;

public enum ItemStatus
{
    Waiting,
    Searching,
    Matched,
    Review,
    NotFound,
    Error,
    Clash,
    Blocked,
    AlreadyNamed,
    Queued,
    Renaming,
    Renamed,
    Failed,
}

/// <summary>One video file in the list.</summary>
public sealed class MediaItem : ObservableObject
{
    private string _path;
    private ParsedMedia _parsed;
    private ItemStatus _status = ItemStatus.Waiting;
    private ItemStatus _matchStatus = ItemStatus.Waiting;
    private bool _isChecked;
    private string? _message;
    private string? _matchMessage;
    private string _newName = "";
    private string? _destinationPath;
    private string? _workText;

    public MediaItem(string path, ParsedMedia parsed)
    {
        _path = path;
        _parsed = parsed;
    }

    public string Path
    {
        get => _path;
        private set
        {
            if (Set(ref _path, value))
            {
                OnPropertyChanged(nameof(FileName));
                OnPropertyChanged(nameof(CurrentTooltip));
                OnPropertyChanged(nameof(Folder));
                OnPropertyChanged(nameof(FolderShort));
            }
        }
    }

    public string FileName => System.IO.Path.GetFileName(_path);

    public string Folder => System.IO.Path.GetDirectoryName(_path) ?? "";

    public string FolderShort => ShortenPath(Folder);

    public string KindLabel => (Info?.Kind ?? Parsed.Kind) == MediaKind.Episode ? "TV episode" : "Movie";

    /// <summary>Glyphs from Segoe MDL2 Assets, which ships with Windows 10 and 11.</summary>
    public string KindGlyph => (Info?.Kind ?? Parsed.Kind) == MediaKind.Episode ? "\uE7F4" : "\uE8B2";

    /// <summary>The second line under the new name: a problem if there is one, otherwise the match.</summary>
    public string Detail
    {
        get
        {
            if (!string.IsNullOrEmpty(Message))
            {
                return Message;
            }

            if (Status == ItemStatus.Waiting)
            {
                return "Not looked up yet";
            }

            if (Status == ItemStatus.Searching)
            {
                return "Looking this up on TMDB";
            }

            if (Info is null)
            {
                return "";
            }

            var destinationFolder = System.IO.Path.GetDirectoryName(DestinationPath ?? "");
            if (!string.IsNullOrEmpty(destinationFolder) && !string.Equals(destinationFolder, Folder, StringComparison.OrdinalIgnoreCase))
            {
                return $"Goes to {ShortenPath(destinationFolder)}";
            }

            var certainty = IsManual ? "your choice" : $"{Confidence:P0} match";
            return $"{KindLabel} · {certainty} · TMDB {Info.TmdbId}";
        }
    }

    public string? NewNameTooltip => DestinationPath ?? Message;

    public ParsedMedia Parsed
    {
        get => _parsed;
        set
        {
            if (Set(ref _parsed, value))
            {
                OnPropertyChanged(nameof(CurrentTooltip));
            }
        }
    }

    public MediaInfo? Info { get; private set; }

    public IReadOnlyList<TmdbCandidate> Candidates { get; private set; } = [];

    public double Confidence { get; private set; }

    /// <summary>True once the user has picked the match, so a re-match leaves it alone.</summary>
    public bool IsManual { get; private set; }

    public string? DestinationPath
    {
        get => _destinationPath;
        private set
        {
            if (Set(ref _destinationPath, value))
            {
                OnPropertyChanged(nameof(CanRename));
                OnPropertyChanged(nameof(Detail));
                OnPropertyChanged(nameof(NewNameTooltip));
            }
        }
    }

    public string NewName
    {
        get => _newName;
        private set => Set(ref _newName, value);
    }

    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }

    public ItemStatus Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusGlyph));
                OnPropertyChanged(nameof(CanRename));
                OnPropertyChanged(nameof(IsWorking));
                OnPropertyChanged(nameof(Detail));
            }
        }
    }

    public string? Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value))
            {
                OnPropertyChanged(nameof(StatusTooltip));
                OnPropertyChanged(nameof(Detail));
                OnPropertyChanged(nameof(NewNameTooltip));
            }
        }
    }

    /// <summary>A failed rename stays available, so it can be retried once the cause is fixed.</summary>
    public bool CanRename => Status is ItemStatus.Matched or ItemStatus.Review or ItemStatus.Failed && DestinationPath is not null;

    public bool IsWorking => Status is ItemStatus.Searching or ItemStatus.Renaming;

    public string StatusText => Status switch
    {
        ItemStatus.Waiting => "Waiting",
        ItemStatus.Searching => "Searching",
        ItemStatus.Matched => "Matched",
        ItemStatus.Review => "Check match",
        ItemStatus.NotFound => "No match",
        ItemStatus.Error => "Error",
        ItemStatus.Clash => "Name clash",
        ItemStatus.Blocked => "Not ready",
        ItemStatus.AlreadyNamed => "Already named",
        ItemStatus.Queued => "Queued",
        ItemStatus.Renaming => _workText ?? "Renaming",
        ItemStatus.Renamed => "Renamed",
        ItemStatus.Failed => "Failed",
        _ => "",
    };

    public string StatusGlyph => Status switch
    {
        ItemStatus.Waiting => "\uE823",
        ItemStatus.Searching => "\uE895",
        ItemStatus.Matched => "\uE73E",
        ItemStatus.Review => "\uE7BA",
        ItemStatus.NotFound => "\uE711",
        ItemStatus.Error => "\uE783",
        ItemStatus.Clash => "\uE7BA",
        ItemStatus.Blocked => "\uE7BA",
        ItemStatus.AlreadyNamed => "\uE73E",
        ItemStatus.Queued => "\uE823",
        ItemStatus.Renaming => "\uE895",
        ItemStatus.Renamed => "\uE73E",
        ItemStatus.Failed => "\uE783",
        _ => "",
    };

    public string StatusTooltip => Message ?? StatusText;

    public string CurrentTooltip
    {
        get
        {
            var read = Parsed.Kind switch
            {
                MediaKind.Episode when Parsed.Season is not null && Parsed.Episode is not null =>
                    $"TV: {Parsed.Title}, S{Parsed.Season:00}E{Parsed.Episode:00}",
                MediaKind.Episode => $"TV: {Parsed.Title}",
                _ => Parsed.Year is null ? $"Movie: {Parsed.Title}" : $"Movie: {Parsed.Title} ({Parsed.Year})",
            };
            return $"{_path}\nRead as {read}";
        }
    }

    public void BeginSearch()
    {
        SetMatchState(ItemStatus.Searching, null);
        ClearName();
    }

    public void ApplyMatch(MatchResult result, bool isManual = false)
    {
        Info = result.Info;
        IsManual = isManual;
        Confidence = result.Confidence;
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(KindGlyph));
        if (result.Candidates.Count > 0)
        {
            Candidates = result.Candidates;
        }

        var status = result.Quality switch
        {
            MatchQuality.Good => ItemStatus.Matched,
            MatchQuality.NeedsReview => ItemStatus.Review,
            _ => ItemStatus.NotFound,
        };
        SetMatchState(status, result.Message);

        // Only confident matches are ticked. A best guess waits for the user to look at it.
        IsChecked = status == ItemStatus.Matched;
    }

    public void SetError(string message)
    {
        Info = null;
        IsChecked = false;
        SetMatchState(ItemStatus.Error, message);
        ClearName();
    }

    public void ResetToWaiting()
    {
        Info = null;
        IsManual = false;
        IsChecked = false;
        Candidates = [];
        SetMatchState(ItemStatus.Waiting, null);
        ClearName();
    }

    /// <summary>Works out the new name. Clashes between items are checked afterwards.</summary>
    public void UpdateName(NameBuilder builder)
    {
        if (_matchStatus == ItemStatus.Renamed)
        {
            return;
        }

        if (Info is null)
        {
            ClearName();
            Status = _matchStatus;
            Message = _matchMessage;
            return;
        }

        var plan = builder.Build(Info, Path, out var error);
        if (plan is null)
        {
            ClearName();
            Status = ItemStatus.Blocked;
            Message = error;
            return;
        }

        DestinationPath = plan.DestinationPath;
        NewName = System.IO.Path.GetFileName(plan.DestinationPath);

        if (_matchStatus == ItemStatus.Failed)
        {
            Status = ItemStatus.Failed;
            Message = _matchMessage;
            return;
        }

        if (string.Equals(plan.DestinationPath, Path, StringComparison.Ordinal))
        {
            Status = ItemStatus.AlreadyNamed;
            Message = "This file already has the right name.";
            return;
        }

        var isCaseChangeOnly = string.Equals(plan.DestinationPath, Path, StringComparison.OrdinalIgnoreCase);
        if (!isCaseChangeOnly && File.Exists(plan.DestinationPath))
        {
            Status = ItemStatus.Clash;
            Message = $"\"{System.IO.Path.GetFileName(plan.DestinationPath)}\" already exists there.";
            return;
        }

        Status = _matchStatus;
        Message = _matchMessage;
    }

    public void MarkClash(string message)
    {
        Status = ItemStatus.Clash;
        Message = message;
    }

    /// <summary>Waiting its turn in a batch that has started.</summary>
    public void MarkQueued() => Status = ItemStatus.Queued;

    /// <summary>Being renamed now. While it is copied to another drive, shows how far it has got.</summary>
    public void ShowWorking(double? copied)
    {
        var text = copied is { } fraction ? $"Copying {fraction:P0}" : null;
        var changed = text != _workText;
        _workText = text;
        if (Status != ItemStatus.Renaming)
        {
            Status = ItemStatus.Renaming;
        }
        else if (changed)
        {
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public void MarkRenamed(string newPath, string? warning)
    {
        _workText = null;
        Path = newPath;
        _matchStatus = ItemStatus.Renamed;
        _matchMessage = warning;
        Status = ItemStatus.Renamed;
        Message = warning;
        IsChecked = false;
    }

    public void MarkFailed(string message)
    {
        _workText = null;
        _matchStatus = ItemStatus.Failed;
        _matchMessage = message;
        Status = ItemStatus.Failed;
        Message = message;
        IsChecked = false;
    }

    /// <summary>After an undo: back at the old path with the earlier match still valid.</summary>
    public void Restore(string oldPath)
    {
        Path = oldPath;
        _matchStatus = ItemStatus.Matched;
        _matchMessage = null;
        IsChecked = true;
    }

    /// <summary>Keeps long paths readable by showing the drive and the last two folders.</summary>
    private static string ShortenPath(string path)
    {
        const int Limit = 56;
        if (path.Length <= Limit)
        {
            return path;
        }

        var root = System.IO.Path.GetPathRoot(path) ?? "";
        var folders = path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return folders.Length <= 2 ? path : $"{root}…\\{string.Join('\\', folders.TakeLast(2))}";
    }

    private void SetMatchState(ItemStatus status, string? message)
    {
        _matchStatus = status;
        _matchMessage = message;
        Status = status;
        Message = message;
    }

    private void ClearName()
    {
        DestinationPath = null;
        NewName = "";
    }
}
