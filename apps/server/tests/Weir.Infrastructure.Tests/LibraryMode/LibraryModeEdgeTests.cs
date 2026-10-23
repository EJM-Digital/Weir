using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Jobs;
using Weir.Core.LibraryMode;
using Weir.Core.MediaManagers;
using Weir.Core.Rules;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.Scheduling;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Jobs;
using Weir.Infrastructure.Tests.Media;
using Weir.Infrastructure.Tests.MediaManagers;
using Weir.Infrastructure.Tests.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>Library mode when its stored state or its inputs are not the ordinary kind.</summary>
public sealed class LibraryModeEdgeTests : IDisposable
{
    private const string FilmPath = "/library/films/Arrival (2016)/Arrival.mkv";

    private static readonly ProcessingRulesConfig EnglishOnly = RemuxRules.DefaultConfig() with
    {
        AudioPreferenceMode = RemuxRuleValues.PolicyPreferredLangsStrict,
        PrimaryAudioLang = "eng",
        SecondaryAudioLang = string.Empty,
        TertiaryAudioLang = string.Empty,
    };

    private readonly JobsTestDatabase _db = new();
    private readonly MediaManagerFixture _fixture = new();

    public void Dispose()
    {
        _db.Dispose();
        _fixture.Dispose();
    }

    [Fact]
    public async Task A_library_that_does_not_exist_has_the_empty_settings()
    {
        await using var uow = await UnitOfWork.OpenAsync(_db.Database);

        var settings = await new LibrarySettingsStore().GetAsync(uow, 9999);

        Assert.Equal(LibrarySettings.Empty, settings);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"files\": [")]
    public async Task A_completed_scan_with_no_readable_payload_still_shows_its_files_with_no_scan_time_or_errors(string? payload)
    {
        var logger = new CapturingLogger<LibraryScanStore>();
        await using var uow = await UnitOfWork.OpenAsync(_db.Database);

        var outcome = await new LibraryScanStore().OutcomeAsync(uow, _db.AddLibrary(), new LibraryScanJobRow(3, ProcessingJobStatus.Completed, payload), logger);

        Assert.Equal((DateTimeOffset.UnixEpoch, 0), (outcome!.GeneratedAt, outcome.Errors.Count));
        Assert.Equal(payload is { Length: > 0 }, logger.Logged(LogLevel.Warning, "job_id=3 has an unreadable payload"));
    }

    [Fact]
    public async Task A_workflow_whose_schedule_is_switched_off_leaves_the_task_list_of_the_timer_that_listed_it()
    {
        var settings = new LibrarySettingsStore();
        var libraryId = Convert.ToInt64(await _fixture.Db(uow => uow.ExecuteScalarWriteAsync(
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, display_order) " +
            "VALUES ('Films', 'movie', '/downloads/f', '/output/f', '/work/f', 1) RETURNING id")), CultureInfo.InvariantCulture);
        await _fixture.Db(async uow => { await settings.SetAsync(uow, libraryId, new LibrarySettings(["/library/films"], ScheduleEnabled: true)); return true; });
        var tasks = new PeriodicTaskRegistry(_fixture.Store.Clock);
        var timer = new LibraryModeScheduleTask(
            _fixture.Store.Database, _fixture.Jobs, new LibraryScanStore(), settings, new LibraryStore(), _fixture.Store.Clock, new SuiteSettingsStore(new AuthStore()),
            NullLogger<LibraryModeScheduleTask>.Instance, tasks);
        await timer.RunOnceAsync(CancellationToken.None);
        Assert.Single(tasks.Snapshot());

        await _fixture.Db(async uow => { await settings.SetAsync(uow, libraryId, new LibrarySettings(["/library/films"], ScheduleEnabled: false)); return true; });
        await timer.RunOnceAsync(CancellationToken.None);

        Assert.Empty(tasks.Snapshot());
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(FakeMediaRunner.EnglishOnly, 1)]
    [InlineData(FakeMediaRunner.EnglishAndJapanese, 0)]
    [InlineData("""{"streams":[{"index":0,"codec_type":5}]}""", 1)]
    public async Task A_file_whose_removed_audio_cannot_be_worked_out_is_cleaned_with_no_manager_asked(string? probeJson, int removedAudioCount)
    {
        var file = new LibraryScanFileEntry(
            FilmPath, 1_000_000, 1_700_000_000, LibraryFileClassification.WouldChange, Summary: null, Reason: null,
            RemovedAudioCount: removedAudioCount, RemovedSubtitleCount: 0, ManagerKind: "radarr", ManagerTitle: "Arrival",
            ProbeJson: probeJson, ManagerConnectionId: 7, ManagerTitleId: "3", ManagerFileId: 11, ManagerQualityProfileId: 5);
        var manager = new FakeManagerHttp();

        var results = await LibraryCleanPreflightRunner.RunAsync(
            [file],
            new LibrarySettings([], ScheduleEnabled: false),
            EnglishOnly,
            MediaManagerKinds.Movie,
            new SingleLinkInspector(),
            new RedownloadRiskChecker(new ArrRedownloadRiskGateway(manager)),
            new Dictionary<long, ManagerConnection> { [7] = new("radarr", "Radarr", "http://localhost:7878", "key", 7) },
            CancellationToken.None);

        var result = Assert.Single(results);
        Assert.False(result.Skip);
        Assert.False(result.RedownloadRisk);
        Assert.Empty(manager.Requests);
    }

    [Fact]
    public async Task A_cleaned_copy_that_fails_the_integrity_read_is_refused_with_the_reason_in_plain_words()
    {
        var media = new FakeMediaRunner { IntegrityError = "Invalid data found when processing input" };
        var validator = new RemuxOutputSwapValidator(new MediaTools(media, new FixedResolver(), new ListLogger<MediaTools>(), TimeProvider.System));

        var validation = await validator.ValidateAsync(FilmPath, FilmPath + ".cleaned", originalDurationSeconds: null, CancellationToken.None);

        Assert.False(validation.Passed);
        Assert.False(string.IsNullOrWhiteSpace(validation.Problem));
    }

    private sealed class SingleLinkInspector : IHardlinkInspector
    {
        public int? LinkCount(string path) => 1;
    }
}
