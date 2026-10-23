using Weir.Core.LibraryMode;
using Weir.Core.MediaManagers;
using Weir.Core.Rules;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Tests.MediaManagers;
using Weir.Infrastructure.Tests.Processing.RemuxPass;
using Weir.Tests.Shared;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>
/// skip_if_manager_would_redownload (on unless a library says otherwise) decides what a clean does with a file whose removed audio would drop its
/// title below the cutoff of the manager's quality profile: with the setting on the file is left out of the clean and says why, with it off the
/// file is cleaned and the risk is still recorded against it. The replies are those of <see cref="RedownloadRiskReplies"/>.
/// </summary>
public sealed class LibraryCleanPreflightRunnerTests
{
    private const long ConnectionId = 7;
    private const string FilmPath = "/library/films/Arrival (2016)/Arrival.mkv";

    private static readonly string MultiAudioWarning = RedownloadRiskReplies.MultiAudioWarning("Radarr", "Arrival");

    private static readonly ProcessingRulesConfig EnglishOnly = RemuxRules.DefaultConfig() with
    {
        AudioPreferenceMode = RemuxRuleValues.PolicyPreferredLangsStrict,
        PrimaryAudioLang = "eng",
        SecondaryAudioLang = string.Empty,
        TertiaryAudioLang = string.Empty,
    };

    private static readonly ManagerConnection Radarr = new("radarr", "Radarr", "http://localhost:7878", "key", ConnectionId);

    [Fact]
    public async Task With_the_setting_on_a_file_the_manager_would_fetch_again_is_left_out_of_the_clean_and_says_why()
    {
        var results = await PreflightAsync(skipIfManagerWouldRedownload: true);

        var result = Assert.Single(results);
        Assert.True(result.Skip);
        Assert.True(result.RedownloadRisk);
        Assert.Equal(LibraryProblemKind.ManagerRedownload, result.ProblemKind);
        Assert.Equal([MultiAudioWarning], result.SkipReasons);
        Assert.Equal([$"Arrival.mkv: {MultiAudioWarning}"], LibraryCleanPreflightRunner.WarningMessages(results));
    }

    [Fact]
    public async Task With_the_setting_off_the_same_file_is_cleaned_and_the_risk_is_still_recorded()
    {
        var results = await PreflightAsync(skipIfManagerWouldRedownload: false);

        var result = Assert.Single(results);
        Assert.False(result.Skip);
        Assert.Empty(result.SkipReasons);
        Assert.True(result.RedownloadRisk);
        Assert.Equal(LibraryProblemKind.ManagerRedownload, result.ProblemKind);
        Assert.Equal(MultiAudioWarning, Assert.Single(result.RedownloadWarnings).Message);
        Assert.Empty(LibraryCleanPreflightRunner.WarningMessages(results));
    }

    [Fact]
    public async Task A_file_whose_removed_audio_puts_no_format_at_risk_is_cleaned_with_nothing_to_warn_of()
    {
        var results = await PreflightAsync(skipIfManagerWouldRedownload: true, cutoffFormatScore: 40);

        var result = Assert.Single(results);
        Assert.False(result.Skip);
        Assert.False(result.RedownloadRisk);
        Assert.Empty(LibraryCleanPreflightRunner.WarningMessages(results));
    }

    [Fact]
    public async Task A_file_whose_stored_probe_cannot_be_read_has_no_removed_audio_to_weigh_and_is_cleaned()
    {
        var results = await PreflightAsync(skipIfManagerWouldRedownload: true, probeJson: "{\"streams\": 5}");

        var result = Assert.Single(results);
        Assert.False(result.Skip);
        Assert.False(result.RedownloadRisk);
    }

    [Fact]
    public async Task A_file_whose_link_count_cannot_be_read_is_judged_on_what_else_is_known()
    {
        var results = await PreflightAsync(skipIfManagerWouldRedownload: false, inspector: new RefusingInspector());

        Assert.Single(results);
    }

    private static async Task<List<LibraryFilePreflightResult>> PreflightAsync(bool skipIfManagerWouldRedownload, int cutoffFormatScore = 60, string probeJson = FakeMediaRunner.EnglishAndJapanese, IHardlinkInspector? inspector = null)
    {
        var replies = RedownloadRiskReplies.For(cutoffFormatScore);
        var manager = new FakeManagerHttp()
            .Json(HttpMethod.Get, replies.FilePath, replies.FileJson)
            .Json(HttpMethod.Get, replies.ProfilePath, replies.ProfileJson);
        var file = new LibraryScanFileEntry(
            FilmPath, 1_000_000, 1_700_000_000, LibraryFileClassification.WouldChange, Summary: null, Reason: null,
            RemovedAudioCount: 1, RemovedSubtitleCount: 0, ManagerKind: "radarr", ManagerTitle: "Arrival",
            ProbeJson: probeJson, ManagerConnectionId: ConnectionId, ManagerTitleId: "3",
            ManagerFileId: replies.FileId, ManagerQualityProfileId: replies.ProfileId);

        return await LibraryCleanPreflightRunner.RunAsync(
            [file],
            new LibrarySettings([], ScheduleEnabled: false, SkipIfManagerWouldRedownload: skipIfManagerWouldRedownload),
            EnglishOnly,
            MediaManagerKinds.Movie,
            inspector ?? new SingleLinkInspector(),
            new RedownloadRiskChecker(new ArrRedownloadRiskGateway(manager)),
            new Dictionary<long, ManagerConnection> { [ConnectionId] = Radarr },
            CancellationToken.None);
    }

    private sealed class RefusingInspector : IHardlinkInspector
    {
        public int? LinkCount(string path) => throw new UnauthorizedAccessException();
    }

    [Theory]
    [InlineData(0, "{}")]
    [InlineData(1, null)]
    [InlineData(1, "")]
    [InlineData(1, "this is not ffprobe output")]
    public void A_file_with_nothing_to_remove_or_nothing_readable_to_replan_removes_no_languages(int removedAudioCount, string? probeJson)
    {
        var file = new LibraryScanFileEntry(FilmPath, 1, 1, LibraryFileClassification.WouldChange, null, null, removedAudioCount, 0, "radarr", "Arrival", probeJson);

        Assert.Empty(LibraryCleanPreflightRunner.RemovedAudioLanguages(file, EnglishOnly));
    }

    private sealed class SingleLinkInspector : IHardlinkInspector
    {
        public int? LinkCount(string path) => 1;
    }
}
