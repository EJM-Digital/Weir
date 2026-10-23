using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Observability;
using Weir.Core.Processing;

namespace Weir.Core.Tests.Processing;

/// <summary>The last edges of what Weir decides about a file and how it says so: scopes, schedules, queue rows, titles and diagnostics.</summary>
public sealed class ProcessingRemainingEdgeTests
{
    private static WireObject Obj(string json) => (WireObject)WireJsonParser.Parse(json);

    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("api_token", "abc", "[redacted]")]
    [InlineData("reason", "failed with token=abc, retrying", "failed with token=[redacted], retrying")]
    [InlineData("reason", "nothing secret", "nothing secret")]
    public void A_secret_looking_field_or_assignment_is_hidden(string key, string value, string expected)
    {
        Assert.Equal(expected, Diagnostics.SanitizeText(key, value));
    }

    [Theory]
    [InlineData("failed", "error")]
    [InlineData("Retrying", "warning")]
    [InlineData("warning", "warning")]
    [InlineData("completed", "info")]
    public void A_results_severity_follows_what_it_says(string result, string severity)
    {
        Assert.Equal(severity, Diagnostics.SeverityForResult(result));
    }

    [Fact]
    public void An_event_carries_only_the_optional_fields_it_has()
    {
        var bare = new DiagnosticEvent("processing", "scan", "schedule", "completed", "info", Provider: "", Counts: []).AsSafeDict();
        var full = new DiagnosticEvent("processing", "scan", "schedule", "failed", "error", "radarr", "movie", "c1", "token=abc", "Try again", [new("files", 2)]).AsSafeDict();

        Assert.Equal(["module", "action", "trigger", "result", "severity"], bare.Items.Select(pair => pair.Key));
        Assert.Equal("token=[redacted]", ((WireString)full.Get("reason")!).Value);
        Assert.Equal(2, ((WireInteger)((WireObject)full.Get("counts")!).Get("files")!).Value);
    }

    [Theory]
    [InlineData("tv", "TV episodes")]
    [InlineData("Anime", "Anime")]
    [InlineData(null, null)]
    public void A_media_scope_is_named_in_plain_words_or_left_as_written(string? scope, string? expected)
    {
        Assert.Equal(expected, OperatorMessages.MediaScopeLabel(scope));
    }

    [Fact]
    public void An_activity_envelope_leaves_out_the_messages_that_are_empty()
    {
        var bare = OperatorMessages.ActivityDetailEnvelope("processing", "scan", "schedule", "completed", userMessage: "", nextAction: "");
        var full = OperatorMessages.ActivityDetailEnvelope("processing", "scan", "schedule", "completed", userMessage: "Done", nextAction: "Nothing");

        Assert.Null(bare.Get("user_message"));
        Assert.Null(bare.Get("next_action"));
        Assert.Equal("Done", ((WireString)full.Get("user_message")!).Value);
        Assert.Equal("Nothing", ((WireString)full.Get("next_action")!).Value);
    }

    [Theory]
    [InlineData("Film 1850", false)]
    [InlineData("Film 2500", false)]
    [InlineData("Film 1999", true)]
    [InlineData("Film 2000", true)]
    public void A_year_is_only_a_year_within_the_range_of_films(string name, bool hasYear)
    {
        Assert.Equal(hasYear, ProcessingDomain.ExtractTitleYearAnchor(name)?.Year is not null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_name_with_nothing_in_it_has_no_title_anchor(string? raw)
    {
        Assert.Null(ProcessingDomain.ExtractTitleYearAnchor(raw));
    }

    [Fact]
    public void An_anchor_with_no_title_or_no_year_never_matches()
    {
        var noTitle = new TitleYearAnchor(new HashSet<string>(), 1999);
        var noYear = new TitleYearAnchor(new HashSet<string> { "film" }, null);
        var whole = new TitleYearAnchor(new HashSet<string> { "film" }, 1999);

        Assert.False(ProcessingDomain.AnchorsMatch(noTitle, noTitle));
        Assert.False(ProcessingDomain.AnchorsMatch(noYear, noYear));
        Assert.True(ProcessingDomain.AnchorsMatch(whole, whole));
    }

    [Fact]
    public void A_queue_row_owns_a_file_by_title_only_when_a_candidate_is_given()
    {
        var row = new ProcessingQueueRowView(false, true, false, false, "Film", 1999);

        Assert.False(ProcessingDomain.FileIsOwnedByQueue([row]));
        Assert.False(ProcessingDomain.FileIsOwnedByQueue([row], new FileAnchorCandidate("Other", 1999)));
        Assert.True(ProcessingDomain.FileIsOwnedByQueue([row], new FileAnchorCandidate("Film.1999.1080p", 1999)));
    }

    [Theory]
    [InlineData("tv", "TV episodes")]
    [InlineData("movie", "Movies")]
    public void No_manager_for_a_scope_says_which_scope(string scope, string word)
    {
        Assert.Contains(word, CandidateGate.NoManagerConfiguredNote(scope), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, null, "Already done: cleaned on")]
    [InlineData(true, null, "Already imported: your media manager collected")]
    [InlineData(true, "Radarr", "Already imported: Radarr collected")]
    public void A_repeat_says_whether_it_was_cleaned_or_collected(bool collected, string? manager, string expected)
    {
        var earlier = new CleanedEarlier("D:\\Clean\\Film.mkv", Now, collected, manager, CollectedAt: null);

        Assert.StartsWith(expected, earlier.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("D:\\Clean", true)]
    public void A_repeat_names_the_output_folder_only_when_it_is_known(string folder, bool named)
    {
        var result = new CleanedEarlier("D:\\Clean\\Film.mkv", Now, false, null, null).ToResult("Film.mkv", 3, folder);

        Assert.Equal(named, result.Get("processing_output_folder_resolved") is not null);
    }

    private static ProcessingLibraryRecord Library(bool scheduleEnabled = false) =>
        new() { Name = "Movies", Enabled = true, ScheduleEnabled = scheduleEnabled };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void A_file_outside_the_window_is_out_of_schedule_only_when_the_schedule_is_on(bool scheduleEnabled, bool outOfSchedule)
    {
        var verdict = FileStateDecision.DecideFileState(
            Library(scheduleEnabled), false, 1000, null, null, null, false, null, null, null, null, Now);

        Assert.Equal(outOfSchedule, verdict.Status == ProcessingFileStatuses.OutOfSchedule);
    }

    [Fact]
    public void A_file_still_being_written_with_no_reason_given_says_it_is_still_being_written()
    {
        var verdict = FileStateDecision.DecideFileState(Library(), true, 1000, null, null, null, true, null, null, null, null, Now);

        Assert.Equal(ProcessingFileStatuses.OnHold, verdict.Status);
        Assert.Contains("still being written to", verdict.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[a-c]x", "bx", true)]
    [InlineData("[a-c]x", "dx", false)]
    [InlineData("[b-c]x", "ax", false)]
    public void A_range_in_a_pattern_matches_only_inside_both_ends(string pattern, string value, bool matches)
    {
        Assert.Equal(matches, Glob.IsMatch(value, pattern));
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("C:/", "c:")]
    [InlineData("/Data/Films/", "/data/films")]
    public void A_folder_is_compared_in_lower_case_without_its_trailing_slash(string folder, string expected)
    {
        Assert.Equal(expected, LibraryRules.NormalizeFolder(folder));
    }

    [Theory]
    [InlineData("/a", "/a/b", true)]
    [InlineData("/a", "/a", false)]
    [InlineData("/a", "/ab", false)]
    [InlineData("/", "/a", true)]
    [InlineData("/a/b", "/a", false)]
    public void An_ancestor_folder_contains_a_descendant_by_whole_segments(string ancestor, string descendant, bool contained)
    {
        Assert.Equal(contained, LibraryRules.IsAncestor(ancestor, descendant));
    }

    [Theory]
    [InlineData(@"D:\Media\Films")]
    [InlineData(@"\\nas\media\films")]
    [InlineData("/data/films")]
    public void A_folder_in_either_style_is_accepted_wherever_the_tests_run(string folder)
    {
        LibraryRules.ValidateFolderPath("watched", folder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Anime")]
    public void An_unknown_media_scope_is_refused_and_a_known_one_is_lower_cased(string? scope)
    {
        Assert.Throws<ProcessingLibraryException>(() => LibraryRules.ValidateScope(scope));
        Assert.Equal("tv", LibraryRules.ValidateScope(" TV "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void A_workflow_needs_a_name(string? name)
    {
        Assert.Throws<ProcessingLibraryException>(() => LibraryRules.ValidateName(name, []));
    }

    [Fact]
    public void A_rule_set_that_does_not_exist_is_refused_by_id()
    {
        Assert.Throws<ProcessingLibraryException>(() => LibraryRules.ValidateRuleSet(9, exists: false));
        Assert.Equal(9, LibraryRules.ValidateRuleSet(9, exists: true));
    }

    [Theory]
    [InlineData(null, "pass_through")]
    [InlineData("HOLD", "hold")]
    [InlineData("nonsense", "pass_through")]
    public void A_failure_policy_that_is_missing_or_unknown_passes_the_file_through(string? raw, string expected)
    {
        Assert.Equal(expected, ProcessingFailurePolicies.Normalize(raw));
    }

    [Fact]
    public void A_scope_with_no_queue_dialect_is_refused()
    {
        Assert.Throws<ArgumentException>(() => QueueRowMapping.DialectForScope("anime"));
        Assert.Equal(QueueRowMapping.MovieDialect, QueueRowMapping.DialectForScope(" Movie "));
    }

    [Fact]
    public void A_queue_row_with_no_status_has_an_empty_status_and_no_output_path()
    {
        var row = Obj("{}");

        Assert.Equal(string.Empty, QueueRowMapping.PrimaryQueueStatus(row));
        Assert.False(QueueRowMapping.PathMatchesCandidate(row, "/a/b.mkv"));
        Assert.False(QueueRowMapping.PathMatchesCandidate(Obj("""{"outputPath": "/a/b.mkv"}"""), null));
        Assert.False(QueueRowMapping.PathMatchesCandidate(Obj("""{"outputPath": "/a/c.mkv"}"""), "/a/b.mkv"));
    }

    [Fact]
    public void A_queue_rows_title_comes_from_its_entity_when_that_has_one_and_from_the_row_when_it_has_not()
    {
        var fromEntity = QueueRowMapping.MapQueueRowToProcessingView(Obj("""{"status": "downloading", "movie": {"title": "Solaris"}}"""), QueueRowMapping.MovieDialect);
        var fromRow = QueueRowMapping.MapQueueRowToProcessingView(Obj("""{"status": "downloading", "movie": {"id": 1}, "title": "Stalker", "year": 1979}"""), QueueRowMapping.MovieDialect);
        var notAnEntity = QueueRowMapping.MapQueueRowToProcessingView(Obj("""{"status": "downloading", "movie": "x", "name": "Nashville"}"""), QueueRowMapping.MovieDialect);

        Assert.Equal(("Solaris", null), (fromEntity.QueueTitle, fromEntity.QueueYear));
        Assert.Equal(("Stalker", null), (fromRow.QueueTitle, fromRow.QueueYear));
        Assert.Equal("Nashville", notAnEntity.QueueTitle);
    }

    [Theory]
    [InlineData("""{"movieId": 7}""", 7, true)]
    [InlineData("""{"movieId": 8}""", 7, false)]
    [InlineData("""{}""", 7, false)]
    public void A_queue_row_belongs_to_a_file_by_the_entity_id_it_names(string row, long entityId, bool applies)
    {
        var view = QueueRowMapping.MapQueueRowToProcessingView(Obj(row), QueueRowMapping.MovieDialect, candidatePath: null, candidateEntityId: entityId);

        Assert.Equal(applies, view.AppliesToFile);
    }

    private static ManagerQueueSignal Reported(params (string Scope, string Json)[] rows) =>
        new(new ManagerConnection("radarr", "main", "http://m", "k"), SignalStatus.Reported, [.. rows.Select(row => new ManagerQueueRow(row.Scope, Obj(row.Json)))]);

    [Fact]
    public void Rows_for_the_other_scope_are_left_out_and_a_silent_manager_is_named()
    {
        var silent = new ManagerQueueSignal(new ManagerConnection("sonarr", "tv", "http://s", "k"), SignalStatus.Unreachable, [], Detail: null);
        var told = new ManagerQueueSignal(new ManagerConnection("sonarr", "other", "http://o", "k"), SignalStatus.Unreachable, [], Detail: "It said no.");
        var signals = new[] { Reported(("tv", """{"status": "downloading"}"""), ("movie", """{"status": "downloading"}""")), silent, told };

        var rows = ManagerQueueSignals.AttributedQueueRows(signals, "movie");
        var report = ManagerQueueSignals.ReportForSignals(signals);

        Assert.Single(rows);
        Assert.Contains("did not answer.", report.SilentDetails[0], StringComparison.Ordinal);
        Assert.Equal("It said no.", report.SilentDetails[1]);
    }
}
