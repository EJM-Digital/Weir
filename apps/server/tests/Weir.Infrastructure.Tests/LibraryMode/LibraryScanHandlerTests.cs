using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Jobs;
using Weir.Core.LibraryMode;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Media;
using Weir.Infrastructure.Tests.MediaManagers;
using Weir.Infrastructure.Tests.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>
/// #505 point 2: the scan job walks library folders, probes (cached by path/size/mtime), classifies with the library's
/// rules, and never writes to a file it looked at.
/// </summary>
public sealed partial class LibraryScanHandlerTests : IDisposable
{
    private readonly MediaManagerFixture _fixture = new();
    private readonly TempDirectory _libraryFolder = new();
    private readonly FakeMediaRunner _media = new();
    private readonly DataChangePublisher _changes = new();
    private readonly LibraryScanProgress _progress = new();
    private readonly LibraryScanStore _scans;
    private readonly LibrarySettingsStore _librarySettings = new();
    private readonly LibraryFileMarksStore _fileMarks = new();
    private readonly LibraryViewStore _libraryView = new();
    private readonly LibraryStore _libraries = new();

    public LibraryScanHandlerTests() => _scans = new LibraryScanStore(_changes, _progress);

    public void Dispose()
    {
        _libraryFolder.Dispose();
        _fixture.Dispose();
    }

    private LibraryScanHandler Handler() => (LibraryScanHandler)Activator.CreateInstance(typeof(LibraryScanHandler), HandlerArguments())!;

    /// <summary>What the handler is built from, in the order of its constructor.</summary>
    private object?[] HandlerArguments() =>
    [
        _fixture.Store.Database,
        new MediaTools(_media, new FixedResolver(), new ListLogger<MediaTools>(), TimeProvider.System),
        _fixture.Connections,
        PhysicalHardlinkInspector.Instance,
        _fixture.Jobs,
        new RedownloadRiskChecker(new ArrRedownloadRiskGateway(_fixture.Http)),
        _scans,
        _librarySettings,
        _fileMarks,
        _libraryView,
        _libraries,
        _progress,
        _fixture.Store.Clock,
        NullLogger<LibraryScanHandler>.Instance,
        _changes,
    ];

    private async Task<long> LibraryAsync() =>
        Convert.ToInt64(await _fixture.Db(uow => uow.ExecuteScalarWriteAsync(
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, display_order) " +
            "VALUES ('Movies library', 'movie', '/downloads/watched', '/downloads/output', '/downloads/work', 1) RETURNING id")), CultureInfo.InvariantCulture);

    private Task<long> EnqueueScanAsync(long libraryId) =>
        _fixture.Store.WithUnitOfWork(async uow => (await _scans.RequestScanAsync(uow, _fixture.Jobs, libraryId, "manual")).Id);

    /// <summary>Claims, runs and completes one scan job exactly as the real worker would, so its row ends up
    /// <c>completed</c> — <see cref="LibraryScanStore.OutcomeAsync"/> reads the scan time and errors from a completed row.</summary>
    private async Task RunScanAsync(long jobId)
    {
        const string leaseOwner = "test-worker";
        var claimed = await _fixture.Jobs.ClaimNextAsync(leaseOwner, _fixture.Store.Clock.GetUtcNow().AddMinutes(5), _fixture.Store.Clock.GetUtcNow());
        Assert.NotNull(claimed);
        Assert.Equal(jobId, claimed!.Id);
        await Handler().HandleAsync(new JobWorkContext(claimed.Id, claimed.JobKind, claimed.PayloadJson, leaseOwner), CancellationToken.None);
        Assert.True(await _fixture.Jobs.CompleteClaimedAsync(claimed.Id, leaseOwner));
    }

    private Task<List<string>> ScanActivityTitlesAsync() =>
        _fixture.Db(uow => uow.QueryAsync(
            "SELECT title FROM activity_events WHERE event_type = 'library.scan_completed' ORDER BY id",
            reader => SqliteValues.GetString(reader, 0)), commit: false);

    [Fact]
    public async Task A_running_scan_counts_the_files_it_has_looked_at_and_stops_counting_when_it_ends()
    {
        var library = await LibraryAsync();
        await _fixture.Db(async uow => { await _librarySettings.SetAsync(uow, library, new LibrarySettings([_libraryFolder.Path], false)); return true; });
        foreach (var name in new[] { "a.mkv", "b.mkv", "c.mkv" })
        {
            await File.WriteAllBytesAsync(_libraryFolder.Join(name), [1, 2, 3]);
        }

        var jobId = await EnqueueScanAsync(library);
        var seenWhileProbing = new List<long?>();
        _media.OnCall = () => seenWhileProbing.Add(_scans.FilesSeen(jobId));

        await RunScanAsync(jobId);

        Assert.Equal([null, 1L, 2L], seenWhileProbing);
        Assert.Null(_scans.FilesSeen(jobId));
    }

    [Fact]
    public async Task A_scan_classifies_files_and_writes_nothing_to_them()
    {
        var library = await LibraryAsync();
        await _fixture.Db(async uow => { await _librarySettings.SetAsync(uow, library, new LibrarySettings([_libraryFolder.Path], false)); return true; });

        var matchingPath = _libraryFolder.Join("english-only.mkv");
        var changingPath = _libraryFolder.Join("english-and-japanese.mkv");
        await File.WriteAllBytesAsync(matchingPath, [1, 2, 3]);
        await File.WriteAllBytesAsync(changingPath, [4, 5, 6]);
        _media.Probes["english-only.mkv"] = FakeMediaRunner.EnglishOnly;
        _media.Probes["english-and-japanese.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        var matchingBytesBefore = File.ReadAllBytes(matchingPath);
        var matchingWriteTimeBefore = File.GetLastWriteTimeUtc(matchingPath);
        var changingBytesBefore = File.ReadAllBytes(changingPath);
        var changingWriteTimeBefore = File.GetLastWriteTimeUtc(changingPath);

        var jobId = await EnqueueScanAsync(library);
        await RunScanAsync(jobId);

        // Never writes: content and mtime of every scanned file are exactly as before.
        Assert.Equal(matchingBytesBefore, File.ReadAllBytes(matchingPath));
        Assert.Equal(matchingWriteTimeBefore, File.GetLastWriteTimeUtc(matchingPath));
        Assert.Equal(changingBytesBefore, File.ReadAllBytes(changingPath));
        Assert.Equal(changingWriteTimeBefore, File.GetLastWriteTimeUtc(changingPath));

        var files = await _fixture.Db(uow => _scans.CurrentFilesAsync(uow, library), commit: false);
        Assert.Equal(2, files.Count);
        var matching = files.Single(f => f.Path == matchingPath);
        var changing = files.Single(f => f.Path == changingPath);
        Assert.Equal(LibraryFileClassification.Matches, matching.Classification);
        Assert.Equal(LibraryFileClassification.WouldChange, changing.Classification);
        Assert.Equal(1, changing.RemovedAudioCount);

        // Unmatched (no manager linked to this library): still processable, not skipped.
        Assert.Null(changing.ManagerKind);
        Assert.Null(changing.ManagerTitle);

        Assert.Equal(["Scanned Movies library: 2 files, 1 would change, 0 could not be processed"], await ScanActivityTitlesAsync());
    }

    private static async Task<string> NextTopicAsync(BroadcastSubscription<string> subscription)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var topic in subscription.ReadAllAsync(timeout.Token))
        {
            return topic;
        }

        throw new InvalidOperationException("The subscription ended.");
    }

    [Fact]
    public async Task A_scan_that_is_queued_says_so_once_it_is_committed_and_not_before()
    {
        var library = await LibraryAsync();
        using var subscription = _changes.Subscribe();

        await _fixture.Store.WithUnitOfWork(
            async uow => (await _scans.RequestScanAsync(uow, _fixture.Jobs, library, "manual")).Id, commit: false);
        _changes.Publish(DataTopics.Settings);
        Assert.Equal(DataTopics.Settings, await NextTopicAsync(subscription));

        await EnqueueScanAsync(library);
        Assert.Equal(DataTopics.LibraryScan, await NextTopicAsync(subscription));
    }

    [Fact]
    public async Task A_scan_says_on_library_scan_as_it_indexes_its_files()
    {
        var library = await LibraryAsync();
        await _fixture.Db(async uow => { await _librarySettings.SetAsync(uow, library, new LibrarySettings([_libraryFolder.Path], false)); return true; });
        await File.WriteAllBytesAsync(_libraryFolder.Join("film.mkv"), [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishOnly;
        var jobId = await EnqueueScanAsync(library);
        using var subscription = _changes.Subscribe();

        await RunScanAsync(jobId);
        _changes.Publish(DataTopics.Settings);

        Assert.Equal(DataTopics.LibraryScan, await NextTopicAsync(subscription));
        Assert.Equal(DataTopics.Settings, await NextTopicAsync(subscription));
    }

    [Fact]
    public async Task A_second_scan_reuses_the_cached_probe_for_an_unchanged_file()
    {
        var library = await LibraryAsync();
        await _fixture.Db(async uow => { await _librarySettings.SetAsync(uow, library, new LibrarySettings([_libraryFolder.Path], false)); return true; });
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishOnly;

        var firstJobId = await EnqueueScanAsync(library);
        await RunScanAsync(firstJobId);
        var probesAfterFirst = _media.Probed.Count();
        Assert.True(probesAfterFirst > 0);

        var secondJobId = await EnqueueScanAsync(library);
        await RunScanAsync(secondJobId);

        // The file's size and mtime have not changed, so the cached ffprobe answer is reused: no new probe call.
        Assert.Equal(probesAfterFirst, _media.Probed.Count());

        Assert.Equal("Scanned Movies library: 1 file, 0 would change, 0 could not be processed", (await ScanActivityTitlesAsync())[^1]);
    }

    [Fact]
    public async Task Scanning_a_library_with_no_folders_configured_reports_a_reason_and_probes_nothing()
    {
        var library = await LibraryAsync();
        var jobId = await EnqueueScanAsync(library);
        await RunScanAsync(jobId);

        Assert.Empty(_media.Probed);
        var outcome = await _fixture.Db(async uow => await _scans.OutcomeAsync(uow, library, await _scans.LatestAsync(uow, library), NullLogger.Instance), commit: false);
        Assert.NotNull(outcome);
        Assert.Empty(await _fixture.Db(uow => _scans.CurrentFilesAsync(uow, library), commit: false));
    }

    // --- Scheduled scan and clean --------------------------------------------------------------------

    private Task<List<string>> CleanPayloadsAsync() =>
        _fixture.Db(uow => uow.QueryAsync(
            "SELECT payload_json FROM jobs WHERE job_kind = @kind ORDER BY id",
            reader => SqliteValues.GetString(reader, 0),
            ("@kind", LibraryModeJobKinds.CleanKind)), commit: false);

    private async Task<(string Changing, string SetAside)> ScheduledLibraryFilesAsync(long library)
    {
        await _fixture.Db(async uow => { await _librarySettings.SetAsync(uow, library, new LibrarySettings([_libraryFolder.Path], ScheduleEnabled: true)); return true; });
        var matching = _libraryFolder.Join("english-only.mkv");
        var changing = _libraryFolder.Join("changing.mkv");
        var setAside = _libraryFolder.Join("set-aside.mkv");
        await File.WriteAllBytesAsync(matching, [1]);
        await File.WriteAllBytesAsync(changing, [2]);
        await File.WriteAllBytesAsync(setAside, [3]);
        _media.Probes["english-only.mkv"] = FakeMediaRunner.EnglishOnly;
        _media.Probes["changing.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        _media.Probes["set-aside.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        await _fixture.Db(async uow => { await _fileMarks.SetLeaveAloneAsync(uow, library, setAside, true, _fixture.Store.Clock.GetUtcNow()); return true; });
        return (changing, setAside);
    }

    [Fact]
    public async Task A_scheduled_scan_queues_a_confirmed_clean_for_each_file_that_would_change_and_is_not_set_aside()
    {
        var library = await LibraryAsync();
        var (changing, _) = await ScheduledLibraryFilesAsync(library);

        var jobId = await _fixture.Store.WithUnitOfWork(async uow =>
            (await _scans.RequestScanAsync(uow, _fixture.Jobs, library, LibraryModeSchedule.Trigger, _fixture.Store.Clock.GetUtcNow())).Id);
        await RunScanAsync(jobId);

        // The file that matches has nothing to clean and the one set aside is never cleaned; only one clean is queued,
        // carrying the confirmation given when the schedule was turned on.
        var payload = Assert.Single(await CleanPayloadsAsync());
        Assert.Contains($"\"path\":{System.Text.Json.JsonSerializer.Serialize(changing)}", payload, StringComparison.Ordinal);
        Assert.Contains("\"trigger\":\"schedule\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"confirm_final_removal\":true", payload, StringComparison.Ordinal);
        Assert.Equal(
            "Scanned Movies library: 3 files, 2 would change, 0 could not be processed; 1 file queued to be cleaned",
            (await ScanActivityTitlesAsync())[^1]);
    }

    [Fact]
    public async Task A_scan_someone_asked_for_never_cleans_even_with_the_schedule_on()
    {
        var library = await LibraryAsync();
        await ScheduledLibraryFilesAsync(library);

        await RunScanAsync(await EnqueueScanAsync(library));

        Assert.Empty(await CleanPayloadsAsync());
        Assert.Equal("Scanned Movies library: 3 files, 2 would change, 0 could not be processed", (await ScanActivityTitlesAsync())[^1]);
    }

    [Fact]
    public async Task A_scheduled_scan_whose_schedule_has_since_been_turned_off_only_scans()
    {
        var library = await LibraryAsync();
        await ScheduledLibraryFilesAsync(library);
        var jobId = await _fixture.Store.WithUnitOfWork(async uow =>
            (await _scans.RequestScanAsync(uow, _fixture.Jobs, library, LibraryModeSchedule.Trigger, _fixture.Store.Clock.GetUtcNow())).Id);
        await _fixture.Db(async uow => { await _librarySettings.SetAsync(uow, library, new LibrarySettings([_libraryFolder.Path], ScheduleEnabled: false)); return true; });

        await RunScanAsync(jobId);

        Assert.Empty(await CleanPayloadsAsync());
    }

    // --- #551: manager title matching --------------------------------------------------------------

    [Fact]
    public async Task A_scan_matches_a_file_to_a_fake_radarr_title_when_the_path_is_shared()
    {
        var library = await LibraryAsync();
        await _fixture.Db(async uow => { await _librarySettings.SetAsync(uow, library, new LibrarySettings([_libraryFolder.Path], false)); return true; });
        await _fixture.AddConnectionAsync("radarr");

        var path = _libraryFolder.Join("english-and-japanese.mkv");
        await File.WriteAllBytesAsync(path, [4, 5, 6]);
        _media.Probes["english-and-japanese.mkv"] = FakeMediaRunner.EnglishAndJapanese;

        var jsonPath = path.Replace("\\", "\\\\", StringComparison.Ordinal);
        _fixture.Http.Json(HttpMethod.Get, "/api/v3/movie",
            """[{"id":7,"title":"Blade Runner 2049","qualityProfileId":3,"movieFile":{"id":42,"path":"__PATH__"}}]""".Replace("__PATH__", jsonPath, StringComparison.Ordinal));

        var jobId = await EnqueueScanAsync(library);
        await RunScanAsync(jobId);

        var files = await _fixture.Db(uow => _scans.CurrentFilesAsync(uow, library), commit: false);
        var file = files.Single();
        Assert.Equal("radarr", file.ManagerKind);
        Assert.Equal("Blade Runner 2049", file.ManagerTitle);
        Assert.Equal("7", file.ManagerTitleId);
        Assert.Equal(42L, file.ManagerFileId);
        Assert.Equal(3L, file.ManagerQualityProfileId);
        Assert.NotNull(file.ManagerConnectionId);
    }

    /// <summary>
    /// #508/#551: a scan's own match data (connection id, manager file id, quality profile id) is exactly what
    /// the re-download-risk preflight needs — no separate lookup — so wiring the two together produces a real
    /// warning for a matched Sonarr/Radarr file, not the always-null placeholder #505 shipped with.
    /// </summary>
    [Fact]
    public async Task A_scans_match_data_feeds_a_real_redownload_risk_warning()
    {
        var library = await LibraryAsync();
        await _fixture.Db(async uow => { await _librarySettings.SetAsync(uow, library, new LibrarySettings([_libraryFolder.Path], false)); return true; });
        await _fixture.AddConnectionAsync("radarr");

        var path = _libraryFolder.Join("english-and-japanese.mkv");
        await File.WriteAllBytesAsync(path, [4, 5, 6]);
        _media.Probes["english-and-japanese.mkv"] = FakeMediaRunner.EnglishAndJapanese;

        var jsonPath = path.Replace("\\", "\\\\", StringComparison.Ordinal);
        _fixture.Http.Json(HttpMethod.Get, "/api/v3/movie",
            """[{"id":7,"title":"Blade Runner 2049","qualityProfileId":4,"movieFile":{"id":42,"path":"__PATH__"}}]""".Replace("__PATH__", jsonPath, StringComparison.Ordinal));

        var jobId = await EnqueueScanAsync(library);
        await RunScanAsync(jobId);

        var files = await _fixture.Db(uow => _scans.CurrentFilesAsync(uow, library), commit: false);
        var file = files.Single();
        Assert.NotNull(file.ManagerConnectionId);
        Assert.Equal(42L, file.ManagerFileId);
        Assert.Equal(4L, file.ManagerQualityProfileId);

        var connection = (await _fixture.Db(uow => _fixture.Connections.ConnectionsByIdAsync(uow, [file.ManagerConnectionId!.Value]), commit: false)).Single();
        _fixture.Http
            .Json(HttpMethod.Get, "/api/v3/moviefile/42",
                """{"id":42,"languages":[{"id":1,"name":"English"}],"customFormats":[{"id":9,"name":"Multi-Audio","specifications":[{"implementation":"LanguageSpecification","implementationName":"Language","negate":false,"fields":[{"name":"value","value":1},{"name":"exceptLanguage","value":true}]}]}],"customFormatScore":50}""")
            .Json(HttpMethod.Get, "/api/v3/qualityprofile/4",
                """{"id":4,"upgradeAllowed":true,"cutoffFormatScore":60,"formatItems":[{"id":1,"format":9,"name":"Multi-Audio","score":50}]}""");

        var checker = new RedownloadRiskChecker(new ArrRedownloadRiskGateway(_fixture.Http));
        var risk = await checker.CheckAsync(
            connection, MediaManagerKinds.Movie, file.ManagerFileId!.Value, file.ManagerQualityProfileId!.Value,
            file.ManagerTitle!, ["jpn"], skipIfManagerWouldRedownload: true);

        var preflight = LibraryCleanPreflight.Evaluate(file.Path, HardlinkDecision.Allow, risk);
        Assert.True(preflight.Skip);
        Assert.Contains("Blade Runner 2049", Assert.Single(preflight.SkipReasons), StringComparison.Ordinal);
        Assert.Contains("Radarr", Assert.Single(preflight.SkipReasons), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scan_matches_a_manager_path_by_reverse_translating_its_own_library_root()
    {
        var library = await LibraryAsync();
        await _fixture.Db(async uow => { await _librarySettings.SetAsync(uow, library, new LibrarySettings([_libraryFolder.Path], false)); return true; });
        await _fixture.AddConnectionAsync("radarr");

        Directory.CreateDirectory(_libraryFolder.Join("Blade Runner 2049 (2017)"));
        var path = _libraryFolder.Join("Blade Runner 2049 (2017)", "movie.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["movie.mkv"] = FakeMediaRunner.EnglishOnly;

        // The manager's own root folder ("/movies") differs from Weir's local library folder: the match has to
        // reverse-translate "/movies/Blade Runner 2049 (2017)/movie.mkv" back onto the local folder to find it.
        _fixture.Http
            .Json(HttpMethod.Get, "/api/v3/rootfolder", """[{"id":1,"path":"/movies"}]""")
            .Json(HttpMethod.Get, "/api/v3/movie",
                """[{"id":7,"title":"Blade Runner 2049","qualityProfileId":3,"movieFile":{"id":42,"path":"/movies/Blade Runner 2049 (2017)/movie.mkv"}}]""");

        var jobId = await EnqueueScanAsync(library);
        await RunScanAsync(jobId);

        var files = await _fixture.Db(uow => _scans.CurrentFilesAsync(uow, library), commit: false);
        var file = files.Single();
        Assert.Equal("radarr", file.ManagerKind);
        Assert.Equal("Blade Runner 2049", file.ManagerTitle);
    }

    [Fact]
    public async Task An_unreachable_manager_is_recorded_as_a_scan_error_and_the_file_stays_unmatched()
    {
        var library = await LibraryAsync();
        await _fixture.Db(async uow => { await _librarySettings.SetAsync(uow, library, new LibrarySettings([_libraryFolder.Path], false)); return true; });
        await _fixture.AddConnectionAsync("radarr");
        // No /api/v3/movie route is scripted: the fake HTTP client refuses the connection, which the manager
        // adapter reports as SignalStatus.Unreachable rather than throwing out of ListLibraryFilesAsync.

        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishOnly;

        var jobId = await EnqueueScanAsync(library);
        await RunScanAsync(jobId);

        var files = await _fixture.Db(uow => _scans.CurrentFilesAsync(uow, library), commit: false);
        var file = files.Single();
        Assert.Null(file.ManagerKind);
        Assert.Null(file.ManagerTitle);
        var outcome = await _fixture.Db(async uow => await _scans.OutcomeAsync(uow, library, await _scans.LatestAsync(uow, library), NullLogger.Instance), commit: false);
        Assert.Single(outcome!.Errors);
        Assert.Contains("Radarr", outcome.Errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manager_that_gives_no_file_by_file_listing_leaves_every_file_unmatched_and_is_not_an_error()
    {
        var library = await LibraryAsync();
        await _fixture.Db(async uow => { await _librarySettings.SetAsync(uow, library, new LibrarySettings([_libraryFolder.Path], false)); return true; });
        await _fixture.AddConnectionAsync("deluno");
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishOnly;

        await RunScanAsync(await EnqueueScanAsync(library));

        var file = (await _fixture.Db(uow => _scans.CurrentFilesAsync(uow, library), commit: false)).Single();
        Assert.Null(file.ManagerKind);
        var outcome = await _fixture.Db(async uow => await _scans.OutcomeAsync(uow, library, await _scans.LatestAsync(uow, library), NullLogger.Instance), commit: false);
        Assert.Empty(outcome!.Errors);
    }

    [Fact]
    public async Task A_manager_that_reports_no_files_leaves_every_file_unmatched_and_is_not_an_error()
    {
        var library = await LibraryAsync();
        await _fixture.Db(async uow => { await _librarySettings.SetAsync(uow, library, new LibrarySettings([_libraryFolder.Path], false)); return true; });
        await _fixture.AddConnectionAsync("radarr");
        _fixture.Http.Json(HttpMethod.Get, "/api/v3/movie", "[]");
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishOnly;

        await RunScanAsync(await EnqueueScanAsync(library));

        var file = (await _fixture.Db(uow => _scans.CurrentFilesAsync(uow, library), commit: false)).Single();
        Assert.Null(file.ManagerKind);
        var outcome = await _fixture.Db(async uow => await _scans.OutcomeAsync(uow, library, await _scans.LatestAsync(uow, library), NullLogger.Instance), commit: false);
        Assert.Empty(outcome!.Errors);
    }
}
