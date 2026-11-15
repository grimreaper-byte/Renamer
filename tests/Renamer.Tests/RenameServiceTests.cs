using Renamer.Core;

namespace Renamer.Tests;

public sealed class RenameServiceTests : IDisposable
{
    private static readonly HashSet<string> Subtitles = FileScanner.ParseExtensions(Defaults.SidecarExtensions);

    private readonly string _root = Directory.CreateTempSubdirectory("renamer-tests-").FullName;
    private readonly RenameService _service;

    public RenameServiceTests()
    {
        _service = new RenameService(Path.Combine(_root, "state", "history.json"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Create(string relative, string content = "video")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string At(string relative) => Path.Combine(_root, relative);

    /// <summary>Real directory entries, because File.Exists ignores case on Windows.</summary>
    private static string[] NamesIn(string folder) =>
        Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).ToArray();

    [Fact]
    public async Task RenamesAFile()
    {
        var source = Create("old.mkv");

        var results = await _service.ExecuteAsync([new RenameRequest(source, At("New Name.mkv"))], null);

        Assert.True(results[0].Success);
        Assert.False(File.Exists(source));
        Assert.Equal("video", File.ReadAllText(At("New Name.mkv")));
    }

    [Fact]
    public async Task NeverOverwritesAnExistingFile()
    {
        var source = Create("old.mkv", "new content");
        var existing = Create("taken.mkv", "precious");

        var results = await _service.ExecuteAsync([new RenameRequest(source, existing)], null);

        Assert.False(results[0].Success);
        Assert.Contains("already exists", results[0].Error);
        Assert.Equal("precious", File.ReadAllText(existing));
        Assert.Equal("new content", File.ReadAllText(source));
    }

    [Fact]
    public async Task ChangingOnlyTheCaseOfANameWorks()
    {
        var source = Create("the matrix.mkv");

        var results = await _service.ExecuteAsync([new RenameRequest(source, At("The Matrix.mkv"))], null);

        Assert.True(results[0].Success);
        Assert.Contains("The Matrix.mkv", NamesIn(_root));
        Assert.DoesNotContain("the matrix.mkv", NamesIn(_root));
    }

    [Fact]
    public async Task CreatesMissingFolders()
    {
        var source = Create("old.mkv");
        var destination = At(@"Show (2020)\Season 01\Show - s01e01.mkv");

        var results = await _service.ExecuteAsync([new RenameRequest(source, destination)], null);

        Assert.True(results[0].Success);
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task SubtitlesFollowTheVideoAndKeepTheirLanguageTags()
    {
        var source = Create("old.mkv");
        Create("old.srt");
        Create("old.en.forced.srt");
        Create("old.nfo");
        Create("older.srt");

        var results = await _service.ExecuteAsync([new RenameRequest(source, At("New.mkv"))], Subtitles);

        Assert.True(results[0].Success);
        Assert.True(File.Exists(At("New.srt")));
        Assert.True(File.Exists(At("New.en.forced.srt")));
        Assert.True(File.Exists(At("old.nfo")));
        Assert.True(File.Exists(At("older.srt")));
    }

    [Fact]
    public async Task ASubtitleClashIsAWarningAndLeavesBothFiles()
    {
        var source = Create("old.mkv");
        Create("old.srt", "mine");
        Create("New.srt", "theirs");

        var results = await _service.ExecuteAsync([new RenameRequest(source, At("New.mkv"))], Subtitles);

        Assert.True(results[0].Success);
        Assert.Single(results[0].Warnings);
        Assert.Equal("mine", File.ReadAllText(At("old.srt")));
        Assert.Equal("theirs", File.ReadAllText(At("New.srt")));
    }

    [Fact]
    public async Task OneFailureDoesNotStopTheRest()
    {
        var good = Create("good.mkv");
        var missing = At("missing.mkv");

        var results = await _service.ExecuteAsync(
            [new RenameRequest(missing, At("x.mkv")), new RenameRequest(good, At("y.mkv"))],
            null);

        Assert.False(results[0].Success);
        Assert.True(results[1].Success);
    }

    [Fact]
    public async Task UndoRestoresFilesAndRemovesCreatedFolders()
    {
        var source = Create(@"dl\old.mkv");
        var subtitle = Create(@"dl\old.srt");
        var destination = At(@"lib\Show (2020)\Season 01\new.mkv");
        await _service.ExecuteAsync([new RenameRequest(source, destination)], Subtitles);
        Assert.False(File.Exists(source));

        var undo = await _service.UndoLastAsync();

        Assert.Empty(undo.Errors);
        Assert.Equal(2, undo.Restored.Count);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(subtitle));
        Assert.False(Directory.Exists(At("lib")));
        Assert.Null(_service.LastBatch);
    }

    [Fact]
    public async Task UndoLeavesFoldersThatWereAlreadyThere()
    {
        var source = Create(@"dl\old.mkv");
        Directory.CreateDirectory(At("lib"));
        await _service.ExecuteAsync([new RenameRequest(source, At(@"lib\Movie\new.mkv"))], null);

        await _service.UndoLastAsync();

        Assert.True(Directory.Exists(At("lib")));
        Assert.False(Directory.Exists(At(@"lib\Movie")));
    }

    [Fact]
    public async Task UndoWillNotOverwriteAFileCreatedSince()
    {
        var source = Create("old.mkv", "original");
        await _service.ExecuteAsync([new RenameRequest(source, At("new.mkv"))], null);
        Create("old.mkv", "something newer");

        var undo = await _service.UndoLastAsync();

        Assert.Single(undo.Errors);
        Assert.Equal("something newer", File.ReadAllText(source));
        Assert.Equal("original", File.ReadAllText(At("new.mkv")));
        Assert.NotNull(_service.LastBatch);
    }

    [Fact]
    public async Task HistorySurvivesARestart()
    {
        var source = Create("old.mkv");
        await _service.ExecuteAsync([new RenameRequest(source, At("new.mkv"))], null);

        var afterRestart = new RenameService(Path.Combine(_root, "state", "history.json"));
        var undo = await afterRestart.UndoLastAsync();

        Assert.Single(undo.Restored);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task UndoOnlyTakesBackTheLastBatch()
    {
        var first = Create("a.mkv");
        var second = Create("b.mkv");
        await _service.ExecuteAsync([new RenameRequest(first, At("a2.mkv"))], null);
        await _service.ExecuteAsync([new RenameRequest(second, At("b2.mkv"))], null);

        await _service.UndoLastAsync();

        Assert.True(File.Exists(At("a2.mkv")));
        Assert.True(File.Exists(second));
        Assert.Equal(At("a2.mkv"), _service.LastBatch!.Moves[0].To);
    }

    /// <summary>Collects reports as they happen, rather than posting them on like Progress&lt;T&gt; does.</summary>
    private sealed class Reports(Action<RenameProgress>? onReport = null) : IProgress<RenameProgress>
    {
        public List<RenameProgress> All { get; } = [];

        public void Report(RenameProgress value)
        {
            All.Add(value);
            onReport?.Invoke(value);
        }
    }

    private RenameService CopyingService() =>
        new(Path.Combine(_root, "state", "history.json")) { AlwaysCopy = true, ReportInterval = TimeSpan.Zero };

    private string CreateLarge(string relative, int size, out byte[] content)
    {
        content = new byte[size];
        new Random(1).NextBytes(content);
        var path = Path.Combine(_root, relative);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public async Task CopyingToAnotherDriveReportsBytesAsItGoes()
    {
        const int Size = 5 * 1024 * 1024 + 123;
        var source = CreateLarge("big.mkv", Size, out var content);
        var reports = new Reports();

        var results = await CopyingService().ExecuteAsync([new RenameRequest(source, At(@"lib\Big.mkv"))], null, reports);

        Assert.True(results[0].Success);
        Assert.Equal(content, File.ReadAllBytes(At(@"lib\Big.mkv")));
        Assert.False(File.Exists(source));

        Assert.Contains(reports.All, r => r is { IsCopying: true, BytesDone: > 0 and < Size, FileFraction: > 0 and < 1 });
        Assert.Equal(reports.All.Select(r => r.BytesDone).Order(), reports.All.Select(r => r.BytesDone));
        Assert.Single(reports.All, r => r.Finished is { Success: true });

        var last = reports.All[^1];
        Assert.Equal((1, 1, Size, Size, Size, Size), (last.Completed, last.Total, last.BytesDone, last.BytesTotal, last.CopiedBytes, last.CopyBytesTotal));
        Assert.Null(last.CurrentSource);
    }

    [Fact]
    public async Task RenamesOnTheSameDriveCountInOneGo()
    {
        var first = Create("a.mkv", "12345");
        var second = Create("b.mkv", "123");
        var reports = new Reports();

        await _service.ExecuteAsync([new RenameRequest(first, At("A2.mkv")), new RenameRequest(second, At("B2.mkv"))], null, reports);

        Assert.DoesNotContain(reports.All, r => r.IsCopying);
        Assert.All(reports.All, r => Assert.Equal(0, r.CopyBytesTotal));
        Assert.Equal(8, reports.All[^1].BytesDone);
        Assert.Equal(2, reports.All.Count(r => r.Finished is not null));
    }

    [Fact]
    public async Task StoppingMidCopyKeepsTheOriginalAndLeavesNoPartFile()
    {
        var source = CreateLarge("big.mkv", 4 * 1024 * 1024, out var content);
        using var stop = new CancellationTokenSource();
        var reports = new Reports(r =>
        {
            if (r.IsCopying)
            {
                stop.Cancel();
            }
        });

        var results = await CopyingService().ExecuteAsync([new RenameRequest(source, At(@"lib\Big.mkv"))], null, reports, stop.Token);

        Assert.False(results[0].Success);
        Assert.Equal(content, File.ReadAllBytes(source));
        Assert.Empty(Directory.GetFiles(At("lib")));
    }

    [Fact]
    public async Task UndoReportsItsProgressToo()
    {
        var source = CreateLarge("big.mkv", 3 * 1024 * 1024, out _);
        var service = CopyingService();
        await service.ExecuteAsync([new RenameRequest(source, At(@"lib\Big.mkv"))], null);
        var reports = new Reports();

        var undo = await service.UndoLastAsync(reports);

        Assert.Single(undo.Restored);
        Assert.Contains(reports.All, r => r.IsCopying);
        Assert.Equal(3 * 1024 * 1024, reports.All[^1].BytesDone);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void ScannerExpandsFoldersAndFiltersFiles()
    {
        var movie = Create(@"drop\Movie.2019.mkv");
        var nested = Create(@"drop\sub\deep\Show.S01E01.MP4");
        Create(@"drop\notes.txt");
        Create(@"drop\Movie.2019-sample.mkv");
        Create(@"drop\Sample\clip.mkv");
        var single = Create("single.avi");
        Create("skipme.nfo");

        var scan = new FileScanner(new AppSettings()).Scan(
            [At("drop"), single, At("skipme.nfo"), single, At("does-not-exist")]);

        Assert.Equal(3, scan.Videos.Count);
        Assert.Contains(movie, scan.Videos);
        Assert.Contains(nested, scan.Videos);
        Assert.Contains(single, scan.Videos);
        Assert.Equal(2, scan.NotVideo);
        Assert.Equal(2, scan.Ignored);
    }
}
