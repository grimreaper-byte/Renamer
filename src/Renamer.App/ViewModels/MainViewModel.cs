using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Shell;
using Renamer.App.Services;
using Renamer.Core;

namespace Renamer.App.ViewModels;

public sealed record SummaryChip(string Text, string Tone);

public sealed class MainViewModel : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly RenameService _renamer = new(AppPaths.HistoryFile);

    private AppSettings _settings;
    private FilenameParser _parser;
    private FileScanner _scanner;
    private NameBuilder _names;
    private MediaMatcher? _matcher;

    private CancellationTokenSource? _matchCancel;
    private CancellationTokenSource? _renameCancel;
    private int _matchRuns;
    private int _matchDone;
    private int _matchTotal;
    private bool _authFailed;
    private string _skippedNote = "";

    private bool _isRenaming;
    private bool _isScanning;
    private string _statusText = "";
    private double _progress;
    private bool _isProgressVisible;
    private string _progressDetail = "";
    private bool _isDragging;
    private bool _hasSelection;
    private string? _alert;
    private int _alertTab;
    private bool _isCheckedAll;
    private bool _updatingChecks;

    public MainViewModel()
    {
        _settings = SettingsStore.Load();
        ThemeService.Apply(_settings.Theme);
        _parser = new FilenameParser(_settings);
        _scanner = new FileScanner(_settings);
        _names = new NameBuilder(_settings);
        _matcher = CreateMatcher(_settings);

        Items.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
            {
                foreach (MediaItem item in e.NewItems)
                {
                    item.PropertyChanged += OnItemChanged;
                }
            }

            if (e.OldItems is not null)
            {
                foreach (MediaItem item in e.OldItems)
                {
                    item.PropertyChanged -= OnItemChanged;
                }
            }

            RefreshSummary();
        };

        MatchCommand = new RelayCommand(() => Run(() => MatchListedAsync(everything: false)), () => Items.Count > 0 && !IsMatching && !IsRenaming);
        StopCommand = new RelayCommand(Stop, () => IsMatching || IsRenaming);
        RenameCommand = new RelayCommand(() => Run(RenameAsync), () => ReadyCount > 0 && !IsMatching && !IsRenaming);
        UndoCommand = new RelayCommand(() => Run(UndoAsync), () => CanUndo && !IsMatching && !IsRenaming);
        ClearCommand = new RelayCommand(Clear, () => Items.Count > 0 && !IsRenaming);
        RemoveRenamedCommand = new RelayCommand(RemoveRenamed, () => Items.Any(i => i.Status == ItemStatus.Renamed) && !IsRenaming);

        UpdateAlert();
        RefreshSummary();
    }

    public ObservableCollection<MediaItem> Items { get; } = [];

    public ICommand MatchCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand RenameCommand { get; }

    public ICommand UndoCommand { get; }

    public ICommand ClearCommand { get; }

    public ICommand RemoveRenamedCommand { get; }

    public AppSettings Settings => _settings;

    public MediaMatcher? Matcher => _matcher;

    public IReadOnlyList<string> PresetNames { get; } =
        [.. Defaults.Presets.Select(p => p.Name), Defaults.CustomPreset];

    public string SelectedPreset
    {
        get => _settings.Preset;
        set
        {
            if (value == _settings.Preset || value is null)
            {
                return;
            }

            // "Custom" only means the templates were edited by hand, so choosing it changes nothing.
            var preset = Defaults.Presets.FirstOrDefault(p => p.Name == value);
            if (preset is not null)
            {
                _settings.Templates = preset.Templates.Clone();
            }

            _settings.Preset = value;
            SaveQuietly();
            _names = new NameBuilder(_settings);
            RefreshNames();
            OnPropertyChanged();
        }
    }

    public int DestinationIndex
    {
        get => (int)_settings.Destination;
        set
        {
            if (value < 0 || value == (int)_settings.Destination)
            {
                return;
            }

            _settings.Destination = (DestinationMode)value;
            SaveQuietly();
            _names = new NameBuilder(_settings);
            RefreshNames();
            UpdateAlert();
            OnPropertyChanged();
        }
    }

    public bool HasItems => Items.Count > 0;

    /// <summary>Whether any rows are selected in the list, which Change match and Remove act on.</summary>
    public bool HasSelection
    {
        get => _hasSelection;
        set => Set(ref _hasSelection, value);
    }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_settings.ApiKey);

    public bool IsMatching => _matchRuns > 0;

    public bool IsRenaming
    {
        get => _isRenaming;
        private set
        {
            if (Set(ref _isRenaming, value))
            {
                OnPropertyChanged(nameof(IsBusy));
            }
        }
    }

    public bool IsBusy => IsMatching || IsRenaming;

    public bool IsScanning
    {
        get => _isScanning;
        private set => Set(ref _isScanning, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public double Progress
    {
        get => _progress;
        private set
        {
            if (Set(ref _progress, value))
            {
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(TaskbarProgress));
            }
        }
    }

    public string ProgressPercent => $"{Math.Floor(Progress):0}%";

    /// <summary>Sizes, speed and time left while copying to another drive. Empty otherwise.</summary>
    public string ProgressDetail
    {
        get => _progressDetail;
        private set => Set(ref _progressDetail, value);
    }

    public bool IsProgressVisible
    {
        get => _isProgressVisible;
        private set
        {
            if (Set(ref _isProgressVisible, value))
            {
                OnPropertyChanged(nameof(TaskbarState));
            }
        }
    }

    /// <summary>The same progress on the app's taskbar button, for when the window is behind others.</summary>
    public TaskbarItemProgressState TaskbarState =>
        IsProgressVisible ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.None;

    public double TaskbarProgress => Progress / 100;

    public bool IsDragging
    {
        get => _isDragging;
        set => Set(ref _isDragging, value);
    }

    /// <summary>A problem the user should act on, shown as a strip above the list.</summary>
    public string? Alert
    {
        get => _alert;
        private set
        {
            if (Set(ref _alert, value))
            {
                OnPropertyChanged(nameof(HasAlert));
            }
        }
    }

    public bool HasAlert => !string.IsNullOrEmpty(Alert);

    /// <summary>The Settings tab that fixes the current alert.</summary>
    public int AlertTab
    {
        get => _alertTab;
        private set => Set(ref _alertTab, value);
    }

    public int ReadyCount => Items.Count(i => i.IsChecked && i.CanRename);

    public string RenameLabel
    {
        get
        {
            var verb = _settings.Destination == DestinationMode.MoveToLibrary ? "Move" : "Rename";
            return ReadyCount switch
            {
                0 => verb,
                1 => $"{verb} 1 file",
                _ => $"{verb} {ReadyCount} files",
            };
        }
    }

    /// <summary>The counts along the bottom of the window.</summary>
    public IReadOnlyList<SummaryChip> Chips
    {
        get
        {
            var chips = new List<SummaryChip> { new(Items.Count == 1 ? "1 file" : $"{Items.Count} files", "Neutral") };
            void Add(int count, string label, string tone)
            {
                if (count > 0)
                {
                    chips.Add(new SummaryChip($"{count} {label}", tone));
                }
            }

            var clashes = Items.Count(i => i.Status == ItemStatus.Clash);
            Add(ReadyCount, "ready", "Good");
            Add(Items.Count(i => i.Status == ItemStatus.Renamed), "renamed", "Good");
            Add(Items.Count(i => i.Status == ItemStatus.Review), "to check", "Warn");
            Add(clashes, clashes == 1 ? "name clash" : "name clashes", "Warn");
            Add(Items.Count(i => i.Status == ItemStatus.Blocked), "not ready", "Warn");
            Add(Items.Count(i => i.Status is ItemStatus.NotFound or ItemStatus.Error), "unmatched", "Bad");
            Add(Items.Count(i => i.Status == ItemStatus.Failed), "failed", "Bad");
            return chips;
        }
    }

    public bool CanUndo => _renamer.LastBatch is not null;

    public string UndoLabel
    {
        get
        {
            var batch = _renamer.LastBatch;
            return batch is null
                ? "Undo last rename"
                : $"Undo last rename ({batch.Moves.Count} {(batch.Moves.Count == 1 ? "file" : "files")})";
        }
    }

    /// <summary>Header tick box: ticks or unticks every row that can be renamed.</summary>
    public bool IsCheckedAll
    {
        get => _isCheckedAll;
        set
        {
            if (!Set(ref _isCheckedAll, value))
            {
                return;
            }

            _updatingChecks = true;
            foreach (var item in Items.Where(i => i.CanRename))
            {
                item.IsChecked = value;
            }

            _updatingChecks = false;
            RefreshSummary();
        }
    }

    public async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        IsScanning = true;
        StatusText = "Looking for video files…";
        try
        {
            var scanner = _scanner;
            var parser = _parser;
            var known = new HashSet<string>(Items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);

            var (scan, added) = await Task.Run(() =>
            {
                var result = scanner.Scan(paths);
                var fresh = result.Videos
                    .Where(v => !known.Contains(v))
                    .OrderBy(v => v, NaturalComparer.Instance)
                    .Select(v => new MediaItem(v, parser.Parse(v)))
                    .ToList();
                return (result, fresh);
            });

            foreach (var item in added)
            {
                Items.Add(item);
            }

            StatusText = DescribeScan(added.Count, scan);
            _skippedNote = scan.Ignored > 0 ? $" Skipped {scan.Ignored} sample{(scan.Ignored == 1 ? "" : "s")}." : "";
            OnPropertyChanged(nameof(HasItems));

            if (added.Count > 0 && _settings.AutoMatch && HasApiKey)
            {
                await MatchItemsAsync(added);
            }
        }
        finally
        {
            IsScanning = false;
        }
    }

    public void ShowMessage(string message) => StatusText = message;

    public void Remove(IEnumerable<MediaItem> items)
    {
        foreach (var item in items.ToList())
        {
            Items.Remove(item);
        }

        RefreshNames();
        OnPropertyChanged(nameof(HasItems));
    }

    /// <summary>Applies a manual pick to the given files, which may be every episode of one show.</summary>
    public async Task ApplyManualMatchAsync(IReadOnlyList<(MediaItem Item, ParsedMedia Parsed)> targets, TmdbCandidate candidate)
    {
        if (_matcher is null)
        {
            return;
        }

        var matcher = _matcher;
        foreach (var (item, _) in targets)
        {
            item.BeginSearch();
        }

        await Task.WhenAll(targets.Select(async target =>
        {
            try
            {
                var path = target.Item.Path;
                var result = await Task.Run(() => matcher.ResolveAsync(target.Parsed, candidate, path));
                target.Item.Parsed = target.Parsed;
                target.Item.ApplyMatch(result, isManual: true);

                // Picking the match is itself the review, so a found episode is ready to go.
                target.Item.IsChecked = result.Info is not null;
            }
            catch (TmdbException ex)
            {
                target.Item.SetError(ex.Message);
            }
        }));

        RefreshNames();
    }

    /// <summary>Takes a saved copy of the settings from the Settings window.</summary>
    public void ApplySettings(AppSettings updated)
    {
        var before = _settings;
        var parsingChanged =
            before.Patterns.Count != updated.Patterns.Count ||
            JsonOf(before.Patterns) != JsonOf(updated.Patterns) ||
            JsonOf(before.Rules) != JsonOf(updated.Rules);
        var lookupChanged =
            before.ApiKey != updated.ApiKey ||
            before.Language != updated.Language ||
            before.ApiBaseUrl != updated.ApiBaseUrl;

        _settings = updated;
        SaveQuietly();
        ThemeService.Apply(_settings.Theme);

        _parser = new FilenameParser(_settings);
        _scanner = new FileScanner(_settings);
        _names = new NameBuilder(_settings);
        if (lookupChanged)
        {
            _matcher = CreateMatcher(_settings);
            _authFailed = false;
        }

        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(SelectedPreset));
        OnPropertyChanged(nameof(DestinationIndex));
        OnPropertyChanged(nameof(HasApiKey));
        UpdateAlert();

        if (parsingChanged)
        {
            foreach (var item in Items.Where(i => i is { Status: not ItemStatus.Renamed, IsManual: false }))
            {
                item.Parsed = _parser.Parse(item.Path);
                item.ResetToWaiting();
            }
        }

        RefreshNames();

        if ((parsingChanged || lookupChanged) && _settings.AutoMatch && HasApiKey)
        {
            // A new key or language changes the answers for files that already have a match.
            Run(() => MatchListedAsync(everything: lookupChanged));
        }
    }

    /// <summary>
    /// Looks up files with no match yet. If there are none, the button means "look everything
    /// up again", so it is never a dead end. Matches the user picked by hand are left alone.
    /// </summary>
    private async Task MatchListedAsync(bool everything)
    {
        if (!HasApiKey)
        {
            UpdateAlert();
            return;
        }

        var automatic = Items
            .Where(i => !i.IsManual && i.Status is not (ItemStatus.Renamed or ItemStatus.Renaming or ItemStatus.Searching))
            .ToList();
        var unfinished = automatic
            .Where(i => i.Status is ItemStatus.Waiting or ItemStatus.NotFound or ItemStatus.Error)
            .ToList();

        await MatchItemsAsync(everything || unfinished.Count == 0 ? automatic : unfinished);
    }

    private async void Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Alert = $"Something went wrong: {ex.Message}";
            ErrorLog.Write(ex);
        }
    }

    private async Task MatchItemsAsync(IReadOnlyList<MediaItem> items)
    {
        if (items.Count == 0 || _matcher is null)
        {
            return;
        }

        var matcher = _matcher;

        // A stopped run may still be finishing, and these files must not inherit its cancellation.
        if (_matchCancel is null || _matchCancel.IsCancellationRequested)
        {
            _matchCancel = new CancellationTokenSource();
        }

        var cancel = _matchCancel;

        _matchRuns++;
        _matchTotal += items.Count;
        _authFailed = false;
        NotifyBusy();
        UpdateProgress();

        foreach (var item in items)
        {
            item.BeginSearch();
        }

        await Task.WhenAll(items.Select(item => MatchOneAsync(matcher, item, cancel)));

        var wasStopped = cancel.IsCancellationRequested;
        _matchRuns--;
        if (_matchRuns == 0)
        {
            _matchCancel = null;
            _matchDone = 0;
            _matchTotal = 0;
            IsProgressVisible = false;
            Progress = 0;
            StatusText = (wasStopped ? "Matching stopped." : "Matching finished. Check the new names, then rename.") + _skippedNote;
            _skippedNote = "";
        }

        RefreshNames();
        UpdateAlert();
        NotifyBusy();
    }

    private async Task MatchOneAsync(MediaMatcher matcher, MediaItem item, CancellationTokenSource cancel)
    {
        try
        {
            var (parsed, path) = (item.Parsed, item.Path);
            var result = await Task.Run(() => matcher.MatchAsync(parsed, path, cancel.Token), cancel.Token);
            item.ApplyMatch(result);
            item.UpdateName(_names);
        }
        catch (OperationCanceledException)
        {
            item.ResetToWaiting();
        }
        catch (TmdbAuthException ex)
        {
            item.SetError(ex.Message);

            // Every other request would fail the same way, so stop asking.
            if (!_authFailed)
            {
                _authFailed = true;
                UpdateAlert();
                cancel.Cancel();
            }
        }
        catch (TmdbException ex)
        {
            item.SetError(ex.Message);
        }

        _matchDone++;
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        IsProgressVisible = _matchTotal > 0;
        Progress = _matchTotal == 0 ? 0 : 100.0 * _matchDone / _matchTotal;
        StatusText = $"Looking up {_matchDone} of {_matchTotal} on TMDB…";
    }

    private async Task RenameAsync()
    {
        var targets = Items.Where(i => i.IsChecked && i.CanRename).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var bySource = targets.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        var requests = targets.Select(i => new RenameRequest(i.Path, i.DestinationPath!)).ToList();
        var sidecars = _settings.RenameSidecars ? FileScanner.ParseExtensions(_settings.SidecarExtensions) : null;

        var isMove = _settings.Destination == DestinationMode.MoveToLibrary;
        var cancel = new CancellationTokenSource();
        _renameCancel = cancel;
        BeginTransfer(isMove ? "Moving files…" : "Renaming files…");

        foreach (var item in targets)
        {
            item.MarkQueued();
        }

        // Rows update as each file finishes, rather than all at the end.
        var applied = new HashSet<MediaItem>();
        var isFinished = false;
        var clock = Stopwatch.StartNew();
        var progress = new Progress<RenameProgress>(p =>
        {
            if (isFinished)
            {
                return;
            }

            ShowTransfer(p, clock.Elapsed, isMove ? "Moving" : "Renaming");
            if (p.Finished is { } finished && bySource.TryGetValue(finished.Request.Source, out var done) && applied.Add(done))
            {
                ApplyResult(done, finished);
            }
            else if (p.CurrentSource is { } source && bySource.TryGetValue(source, out var current) && !applied.Contains(current))
            {
                current.ShowWorking(p.IsCopying ? p.FileFraction : null);
            }
        });

        // Off the window's thread, so a long copy cannot make the window stop responding.
        var results = await Task.Run(() => _renamer.ExecuteAsync(requests, sidecars, progress, cancel.Token));
        isFinished = true;

        var succeeded = 0;
        foreach (var result in results)
        {
            succeeded += result.Success ? 1 : 0;
            var item = bySource[result.Request.Source];
            if (applied.Add(item))
            {
                ApplyResult(item, result);
            }
        }

        cancel.Dispose();
        _renameCancel = null;
        EndTransfer();

        var failed = results.Count - succeeded;
        StatusText = failed == 0
            ? $"{(isMove ? "Moved" : "Renamed")} {succeeded} {(succeeded == 1 ? "file" : "files")}."
            : $"{(isMove ? "Moved" : "Renamed")} {succeeded}, {failed} failed. Hover over a failed row to see why. Tick it to try again.";

        RefreshNames();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoLabel));
        NotifyBusy();
    }

    private async Task UndoAsync()
    {
        var cancel = new CancellationTokenSource();
        _renameCancel = cancel;
        BeginTransfer("Putting files back…");

        var isFinished = false;
        var clock = Stopwatch.StartNew();
        var progress = new Progress<RenameProgress>(p =>
        {
            if (!isFinished)
            {
                ShowTransfer(p, clock.Elapsed, "Putting back");
            }
        });

        var undo = await Task.Run(() => _renamer.UndoLastAsync(progress, cancel.Token));
        isFinished = true;
        cancel.Dispose();
        _renameCancel = null;
        EndTransfer();

        var byPath = Items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var move in undo.Restored)
        {
            if (byPath.TryGetValue(move.To, out var item))
            {
                item.Restore(move.From);
            }
        }

        StatusText = undo.Errors.Count == 0
            ? $"Put back {undo.Restored.Count} {(undo.Restored.Count == 1 ? "file" : "files")}."
            : $"Put back {undo.Restored.Count}, {undo.Errors.Count} could not be restored: {undo.Errors[0]}";

        RefreshNames();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoLabel));
        NotifyBusy();
    }

    private void BeginTransfer(string status)
    {
        IsRenaming = true;
        Progress = 0;
        ProgressDetail = "";
        StatusText = status;
        IsProgressVisible = true;
        NotifyBusy();
    }

    private void EndTransfer()
    {
        IsRenaming = false;
        IsProgressVisible = false;
        Progress = 0;
        ProgressDetail = "";
    }

    private void ShowTransfer(RenameProgress p, TimeSpan elapsed, string verb)
    {
        // Measured in bytes, so one big copy fills the bar steadily instead of all at once at the end.
        Progress = p.BytesTotal > 0
            ? 100.0 * Math.Min(p.BytesDone, p.BytesTotal) / p.BytesTotal
            : p.Total == 0 ? 0 : 100.0 * p.Completed / p.Total;
        StatusText = p.CurrentSource is null
            ? "Finishing…"
            : $"{verb} {Math.Min(p.Completed + 1, p.Total)} of {p.Total}: {p.CurrentFile}";
        ProgressDetail = DescribeTransfer(p, elapsed);
    }

    private static void ApplyResult(MediaItem item, RenameResult result)
    {
        if (result.Success)
        {
            item.MarkRenamed(result.Request.Destination, result.Warnings.Count > 0 ? string.Join("\n", result.Warnings) : null);
        }
        else
        {
            item.MarkFailed(result.Error ?? "The file could not be renamed.");
        }
    }

    /// <summary>"1.2 GB of 4.5 GB · 85.3 MB/s · about 40 seconds left", or empty when nothing is copied.</summary>
    private static string DescribeTransfer(RenameProgress p, TimeSpan elapsed)
    {
        if (p.CopyBytesTotal == 0)
        {
            return "";
        }

        var text = $"{FormatBytes(p.BytesDone)} of {FormatBytes(p.BytesTotal)}";

        // Wait a moment before showing a speed, since the first readings jump around.
        if (p.CopiedBytes > 0 && elapsed.TotalSeconds >= 1)
        {
            var bytesPerSecond = p.CopiedBytes / elapsed.TotalSeconds;
            var secondsLeft = (p.CopyBytesTotal - p.CopiedBytes) / bytesPerSecond;
            text += $" · {FormatBytes((long)bytesPerSecond)}/s";
            if (secondsLeft >= 1)
            {
                text += $" · {DescribeTimeLeft(secondsLeft)}";
            }
        }

        return text;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} bytes" : $"{value:0.0} {units[unit]}";
    }

    private static string DescribeTimeLeft(double seconds)
    {
        if (seconds < 10)
        {
            return "a few seconds left";
        }

        if (seconds < 60)
        {
            return $"about {Math.Ceiling(seconds / 5) * 5:0} seconds left";
        }

        var minutes = (int)Math.Ceiling(seconds / 60);
        if (minutes < 60)
        {
            return minutes == 1 ? "about 1 minute left" : $"about {minutes} minutes left";
        }

        return $"about {minutes / 60} h {minutes % 60} min left";
    }

    private void Stop()
    {
        _matchCancel?.Cancel();
        _renameCancel?.Cancel();
        StatusText = "Stopping…";
    }

    private void Clear()
    {
        _matchCancel?.Cancel();
        Items.Clear();
        StatusText = "";
        IsCheckedAll = false;
        OnPropertyChanged(nameof(HasItems));
    }

    private void RemoveRenamed() => Remove(Items.Where(i => i.Status == ItemStatus.Renamed));

    /// <summary>Recomputes every new name, then flags files that would end up with the same one.</summary>
    private void RefreshNames()
    {
        foreach (var item in Items)
        {
            item.UpdateName(_names);
        }

        var groups = Items
            .Where(i => i.CanRename && i.DestinationPath is not null)
            .GroupBy(i => i.DestinationPath!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in groups)
        {
            foreach (var item in group)
            {
                item.MarkClash($"{group.Count()} files would get this name. Remove one, or add {{resolution}} to the template.");
            }
        }

        RefreshSummary();
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(ReadyCount));
        OnPropertyChanged(nameof(RenameLabel));
        OnPropertyChanged(nameof(Chips));
        OnPropertyChanged(nameof(HasItems));

        var selectable = Items.Where(i => i.CanRename).ToList();
        var allTicked = selectable.Count > 0 && selectable.All(i => i.IsChecked);
        if (_isCheckedAll != allTicked)
        {
            _isCheckedAll = allTicked;
            OnPropertyChanged(nameof(IsCheckedAll));
        }

        CommandManager.InvalidateRequerySuggested();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_updatingChecks && e.PropertyName is nameof(MediaItem.IsChecked) or nameof(MediaItem.Status))
        {
            RefreshSummary();
        }
    }

    private void NotifyBusy()
    {
        OnPropertyChanged(nameof(IsMatching));
        OnPropertyChanged(nameof(IsBusy));
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateAlert()
    {
        const int KeyTab = 0;
        const int NamingTab = 1;

        if (!HasApiKey)
        {
            AlertTab = KeyTab;
            Alert = "Add your TMDB API key in Settings to look up movies and shows.";
        }
        else if (_authFailed)
        {
            AlertTab = KeyTab;
            Alert = "TMDB rejected the API key. Check it in Settings.";
        }
        else if (_settings.Destination == DestinationMode.MoveToLibrary &&
                 (string.IsNullOrWhiteSpace(_settings.MovieLibrary) || string.IsNullOrWhiteSpace(_settings.TvLibrary)))
        {
            AlertTab = NamingTab;
            Alert = "Moving to a library needs a movie folder and a TV folder. Set them in Settings.";
        }
        else
        {
            Alert = null;
        }
    }

    private void SaveQuietly()
    {
        try
        {
            SettingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Alert = $"Settings could not be saved: {ex.Message}";
        }
    }

    private static MediaMatcher? CreateMatcher(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.ApiKey)
            ? null
            : new MediaMatcher(CreateClient(settings));

    public static TmdbClient CreateClient(AppSettings settings) =>
        new(Http, settings.ApiKey, settings.Language, settings.ApiBaseUrl);

    private static string JsonOf<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static string DescribeScan(int added, ScanResult scan)
    {
        var text = added switch
        {
            0 when scan.Videos.Count > 0 => "Those files are already in the list.",
            0 => "No video files found there.",
            1 => "Added 1 file.",
            _ => $"Added {added} files.",
        };

        return scan.Ignored > 0 ? $"{text} Skipped {scan.Ignored} sample{(scan.Ignored == 1 ? "" : "s")}." : text;
    }

    /// <summary>Sorts names the way File Explorer does, so "Episode 2" comes before "Episode 10".</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string left, string right);
    }
}
