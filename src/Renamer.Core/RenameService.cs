using System.Diagnostics;
using System.Text.Json;

namespace Renamer.Core;

public sealed record RenameRequest(string Source, string Destination);

public sealed record RenameResult(RenameRequest Request, bool Success, string? Error, IReadOnlyList<string> Warnings);

/// <summary>
/// Where a batch has got to. Bytes count every file by its size: a rename on the same drive counts in
/// one go, and a copy to another drive counts as it goes. CopiedBytes and CopyBytesTotal only cover
/// copies to another drive, since those are what take the time.
/// </summary>
/// <param name="Completed">Files finished so far.</param>
/// <param name="CurrentFile">Name of the file being worked on, or empty once the batch is done.</param>
/// <param name="CurrentSource">Full path of the file being worked on, or null once the batch is done.</param>
/// <param name="IsCopying">True while the current file is being copied to another drive.</param>
/// <param name="FileFraction">How much of the current file is done, from 0 to 1.</param>
/// <param name="Finished">The result of a file that has just finished, on the report that says so.</param>
public sealed record RenameProgress(
    int Completed,
    int Total,
    string CurrentFile,
    string? CurrentSource,
    bool IsCopying,
    double FileFraction,
    long BytesDone,
    long BytesTotal,
    long CopiedBytes,
    long CopyBytesTotal,
    RenameResult? Finished = null);

public sealed class RenameMove
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public sealed class RenameBatch
{
    public DateTime When { get; set; }
    public List<RenameMove> Moves { get; set; } = [];
    public List<string> CreatedFolders { get; set; } = [];
}

public sealed record UndoResult(IReadOnlyList<RenameMove> Restored, IReadOnlyList<string> Errors);

/// <summary>
/// Renames and moves files without ever overwriting one. Every batch is written to a history file
/// as it runs, so it can be undone later, including after a crash or restart.
/// </summary>
public sealed class RenameService(string historyPath)
{
    private const int MaxBatches = 20;
    private const int CopyBufferSize = 1024 * 1024;
    private const string PartialSuffix = ".renamer-partial";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _historyPath = historyPath;
    private List<RenameBatch>? _history;

    public RenameBatch? LastBatch => LoadHistory().LastOrDefault();

    /// <summary>Copies even on the same drive. Lets tests exercise the copy path with one drive.</summary>
    internal bool AlwaysCopy { get; init; }

    /// <summary>Shortest gap between progress reports during a copy.</summary>
    internal TimeSpan ReportInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public async Task<IReadOnlyList<RenameResult>> ExecuteAsync(
        IReadOnlyList<RenameRequest> requests,
        ISet<string>? sidecarExtensions,
        IProgress<RenameProgress>? progress = null,
        CancellationToken ct = default)
    {
        var batch = new RenameBatch { When = DateTime.Now };
        var results = new List<RenameResult>(requests.Count);

        var sizes = requests.Select(r => SizeOf(r.Source)).ToArray();
        var copyBytes = requests.Select((r, i) => NeedsCopy(r.Source, r.Destination) ? sizes[i] : 0).Sum();
        var tracker = new ProgressTracker(progress, ReportInterval, requests.Count, sizes.Sum(), copyBytes);

        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            var size = sizes[i];
            tracker.Begin(request.Source);

            RenameResult result;
            if (ct.IsCancellationRequested)
            {
                result = new RenameResult(request, false, "Stopped before this file.", []);
            }
            else
            {
                try
                {
                    var sidecars = sidecarExtensions is null ? [] : FindSidecars(request, sidecarExtensions);
                    await MoveAsync(request.Source, request.Destination, batch, done => tracker.Copied(done, size), ct);
                    var warnings = await MoveSidecarsAsync(sidecars, batch, ct);
                    result = new RenameResult(request, true, null, warnings);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    result = new RenameResult(request, false, ex.Message, []);
                }
            }

            results.Add(result);
            if (batch.Moves.Count > 0)
            {
                SaveBatch(batch);
            }

            tracker.End(size, result);
        }

        tracker.Done();
        return results;
    }

    /// <summary>Puts back everything in the most recent batch, then forgets that batch.</summary>
    public async Task<UndoResult> UndoLastAsync(IProgress<RenameProgress>? progress = null, CancellationToken ct = default)
    {
        var history = LoadHistory();
        var batch = history.LastOrDefault();
        if (batch is null)
        {
            return new UndoResult([], []);
        }

        var restored = new List<RenameMove>();
        var errors = new List<string>();
        var unused = new RenameBatch();

        var moves = Enumerable.Reverse(batch.Moves).ToList();
        var sizes = moves.Select(m => SizeOf(m.To)).ToArray();
        var copyBytes = moves.Select((m, i) => NeedsCopy(m.To, m.From) ? sizes[i] : 0).Sum();
        var tracker = new ProgressTracker(progress, ReportInterval, moves.Count, sizes.Sum(), copyBytes);

        for (var i = 0; i < moves.Count; i++)
        {
            var move = moves[i];
            var size = sizes[i];
            tracker.Begin(move.To);
            try
            {
                await MoveAsync(move.To, move.From, unused, done => tracker.Copied(done, size), ct);
                restored.Add(move);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                errors.Add($"{Path.GetFileName(move.To)}: {ex.Message}");
            }

            tracker.End(size, null);
        }

        tracker.Done();

        foreach (var folder in batch.CreatedFolders.OrderByDescending(f => f.Length))
        {
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover empty folder does no harm.
            }
        }

        // Anything that could not go back stays recorded so it can be retried.
        if (errors.Count == 0)
        {
            history.Remove(batch);
        }
        else
        {
            batch.Moves.RemoveAll(restored.Contains);
        }

        WriteHistory(history);
        return new UndoResult(restored, errors);
    }

    /// <summary>Finds subtitles sharing the video's name and works out where they should go.</summary>
    public static IReadOnlyList<RenameRequest> FindSidecars(RenameRequest video, ISet<string> extensions)
    {
        var directory = Path.GetDirectoryName(video.Source);
        if (directory is null || !Directory.Exists(directory))
        {
            return [];
        }

        var oldStem = Path.GetFileNameWithoutExtension(video.Source);
        var newStem = Path.GetFileNameWithoutExtension(video.Destination);
        var newDirectory = Path.GetDirectoryName(video.Destination) ?? directory;

        var sidecars = new List<RenameRequest>();
        foreach (var file in Directory.EnumerateFiles(directory, oldStem + ".*"))
        {
            if (string.Equals(file, video.Source, StringComparison.OrdinalIgnoreCase) || !extensions.Contains(Path.GetExtension(file)))
            {
                continue;
            }

            // Keeps language tags: "Old.en.forced.srt" becomes "New.en.forced.srt".
            var suffix = Path.GetFileName(file)[oldStem.Length..];
            sidecars.Add(new RenameRequest(file, Path.Combine(newDirectory, newStem + suffix)));
        }

        return sidecars;
    }

    private static long SizeOf(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private bool NeedsCopy(string source, string destination) =>
        AlwaysCopy || !string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<string>> MoveSidecarsAsync(IReadOnlyList<RenameRequest> sidecars, RenameBatch batch, CancellationToken ct)
    {
        var warnings = new List<string>();
        foreach (var sidecar in sidecars)
        {
            try
            {
                await MoveAsync(sidecar.Source, sidecar.Destination, batch, null, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{Path.GetFileName(sidecar.Source)} was left behind: {ex.Message}");
            }
        }

        return warnings;
    }

    /// <param name="onCopied">Told how many bytes are done so far, while copying to another drive.</param>
    private async Task MoveAsync(string source, string destination, RenameBatch batch, Action<long>? onCopied, CancellationToken ct)
    {
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"\"{source}\" no longer exists.");
        }

        // Windows treats names as case-insensitive, so "a.mkv" to "A.mkv" is the same file, not a clash.
        var isSameFile = string.Equals(source, destination, StringComparison.OrdinalIgnoreCase);
        if (!isSameFile && File.Exists(destination))
        {
            throw new IOException($"\"{Path.GetFileName(destination)}\" already exists, so the file was not moved.");
        }

        CreateFolders(destination, batch);

        // A case-only rename has to be a real rename, since copying would clash with the file itself.
        if (isSameFile || !NeedsCopy(source, destination))
        {
            await Task.Run(() => File.Move(source, destination, overwrite: false), ct);
        }
        else
        {
            await CopyThenDeleteAsync(source, destination, onCopied, ct);
        }

        batch.Moves.Add(new RenameMove { From = source, To = destination });
    }

    /// <summary>
    /// Copies to a temporary name first, so the final name only ever appears on a complete file and
    /// the original is only deleted once the copy has been checked.
    /// </summary>
    private static async Task CopyThenDeleteAsync(string source, string destination, Action<long>? onCopied, CancellationToken ct)
    {
        var partial = destination + PartialSuffix;
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true))
            {
                var buffer = new byte[CopyBufferSize];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    onCopied?.Invoke(done);
                }
            }

            if (new FileInfo(partial).Length != new FileInfo(source).Length)
            {
                throw new IOException("The copy is a different size from the original.");
            }

            File.SetLastWriteTimeUtc(partial, File.GetLastWriteTimeUtc(source));
            File.Move(partial, destination, overwrite: false);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        File.Delete(source);
    }

    private static void CreateFolders(string destination, RenameBatch batch)
    {
        var folder = Path.GetDirectoryName(destination);
        if (folder is null)
        {
            return;
        }

        var missing = new List<string>();
        for (var current = folder; current is not null && !Directory.Exists(current); current = Path.GetDirectoryName(current))
        {
            missing.Add(current);
        }

        Directory.CreateDirectory(folder);
        batch.CreatedFolders.AddRange(missing.Where(m => !batch.CreatedFolders.Contains(m, StringComparer.OrdinalIgnoreCase)));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done about a stray temporary file here.
        }
    }

    private void SaveBatch(RenameBatch batch)
    {
        var history = LoadHistory();
        if (!history.Contains(batch))
        {
            history.Add(batch);
            if (history.Count > MaxBatches)
            {
                history.RemoveRange(0, history.Count - MaxBatches);
            }
        }

        WriteHistory(history);
    }

    private List<RenameBatch> LoadHistory()
    {
        if (_history is not null)
        {
            return _history;
        }

        try
        {
            _history = File.Exists(_historyPath)
                ? JsonSerializer.Deserialize<List<RenameBatch>>(File.ReadAllText(_historyPath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _history = [];
        }

        return _history;
    }

    private void WriteHistory(List<RenameBatch> history)
    {
        var folder = Path.GetDirectoryName(_historyPath);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var temporary = _historyPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(history, Json));
        File.Move(temporary, _historyPath, overwrite: true);
    }

    /// <summary>
    /// Keeps the running totals for progress reports. Reports during a copy are spaced out, about ten a
    /// second by default, so a fast drive does not flood the window with updates.
    /// </summary>
    private sealed class ProgressTracker(
        IProgress<RenameProgress>? sink,
        TimeSpan interval,
        int total,
        long bytesTotal,
        long copyBytesTotal)
    {
        private readonly Stopwatch _sinceReport = Stopwatch.StartNew();
        private int _completed;
        private long _finishedBytes;
        private long _finishedCopied;
        private long _currentCopied;
        private string _file = "";
        private string? _source;
        private bool _isCopying;
        private double _fraction;

        public void Begin(string source)
        {
            _file = Path.GetFileName(source);
            _source = source;
            _isCopying = false;
            _fraction = 0;
            _currentCopied = 0;
            Send(null);
        }

        public void Copied(long done, long size)
        {
            _isCopying = true;
            _currentCopied = done;
            _fraction = size == 0 ? 1 : Math.Min(1, (double)done / size);
            if (_sinceReport.Elapsed >= interval)
            {
                Send(null);
            }
        }

        public void End(long size, RenameResult? result)
        {
            // Even a failed file counts as dealt with, so the bar keeps moving past it.
            _finishedBytes += size;
            _finishedCopied += _currentCopied;
            _currentCopied = 0;
            _completed++;
            _fraction = 1;
            Send(result);
        }

        public void Done()
        {
            _file = "";
            _source = null;
            _isCopying = false;
            Send(null);
        }

        private void Send(RenameResult? finished)
        {
            _sinceReport.Restart();
            sink?.Report(new RenameProgress(
                _completed,
                total,
                _file,
                _source,
                _isCopying,
                _fraction,
                _finishedBytes + _currentCopied,
                bytesTotal,
                _finishedCopied + _currentCopied,
                copyBytesTotal,
                finished));
        }
    }
}
