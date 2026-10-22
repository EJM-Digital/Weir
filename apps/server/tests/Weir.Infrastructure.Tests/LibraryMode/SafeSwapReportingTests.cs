using Microsoft.Extensions.Logging;
using Weir.Core.LibraryMode;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Processing;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>
/// What a swap tells the person and the log when it does not go cleanly: which step failed, which warnings came with a
/// committed swap, and what was recorded about an abandoned one.
/// </summary>
public sealed class SafeSwapReportingTests
{
    private const string Original = SwapScenario.Original;
    private static readonly string Temp = SwapScenario.Temp;
    private static readonly string Backup = SwapScenario.Backup;

    private sealed class RecordingOwnership : IOutputOwnership
    {
        public List<string> Files { get; } = [];

        public void ApplyToFile(string path) => Files.Add(path);

        public void ApplyToDirectory(string path)
        {
        }
    }

    /// <summary>The failure of the Nth time the swap asks for this file operation.</summary>
    private static void FailOn(SwapScenario scenario, string operation, int occurrence = 1, Func<Exception>? exception = null)
    {
        var seen = 0;
        scenario.Files.OnStep = step =>
        {
            if (step == operation && ++seen == occurrence)
            {
                throw (exception ?? (() => new IOException("the disk said no")))();
            }
        };
    }

    private static bool Logged(SwapScenario scenario, LogLevel level, string fragment) =>
        scenario.SwapLogger.Entries.Any(entry => entry.Level == level && entry.Message.Contains(fragment, StringComparison.Ordinal));

    public static TheoryData<string, int, string> StepsBeforeTheCommit() => new()
    {
        { $"Fingerprint({Original})", 1, "checking the file before starting" },
        { "Journal(Writing)", 1, "recording that the swap started" },
        { $"FileExists({Temp})", 3, "writing the cleaned copy" },
        { $"FileExists({Original})", 2, "checking the original again" },
        { "Journal(Committing)", 1, "recording that the swap is committing" },
        { $"Move({Original} -> {Backup})", 1, "moving the original aside" },
        { $"Fingerprint({Backup})", 1, "checking the original again" },
        { $"Move({Temp} -> {Original})", 1, "putting the cleaned copy in place" },
    };

    [Theory]
    [MemberData(nameof(StepsBeforeTheCommit))]
    public async Task A_failure_at_a_step_names_that_step_and_keeps_the_original(string operation, int occurrence, string stage)
    {
        var s = new SwapScenario();
        FailOn(s, operation, occurrence);

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Failed, result.Outcome);
        Assert.False(result.Committed);
        Assert.Equal(SafeSwapRules.FailedMessage(stage, "the disk said no"), result.Message);
        Assert.Equal([Original], s.Files.Paths);
        Assert.Equal("OLD", s.Files.Read(Original));
    }

    [Fact]
    public async Task A_validator_that_throws_is_reported_as_the_check_of_the_cleaned_copy()
    {
        var s = new SwapScenario();
        s.Validator.Throw = new InvalidOperationException("ffprobe crashed");

        var result = await s.RunAsync();

        Assert.Equal(SafeSwapRules.FailedMessage("checking the cleaned copy", "ffprobe crashed"), result.Message);
        Assert.True(Logged(s, LogLevel.Warning, "Library swap rolled back"));
    }

    [Fact]
    public async Task A_writer_that_writes_nothing_says_so_and_names_the_writing_step()
    {
        var s = new SwapScenario { Writer = (_, _) => Task.CompletedTask };

        var result = await s.RunAsync();

        Assert.Equal(SafeSwapRules.FailedMessage("writing the cleaned copy", "no cleaned copy was written"), result.Message);
        Assert.Equal(SwapJournalState.RolledBack, s.Journal.Latest(SwapScenario.JobId)!.State);
    }

    [Fact]
    public async Task An_earlier_swap_that_cannot_be_cleared_up_says_so_before_any_work()
    {
        var s = new SwapScenario();
        FailOn(s, $"FileExists({Temp})", occurrence: 2);

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Failed, result.Outcome);
        Assert.Equal(SafeSwapRules.FailedMessage("clearing up an earlier interrupted swap", "its leftover files could not be removed"), result.Message);
        Assert.Empty(s.WrittenTo);
        Assert.Empty(s.Journal.History);
    }

    [Fact]
    public async Task A_file_that_vanishes_during_the_preflight_is_a_missing_file()
    {
        var s = new SwapScenario();
        FailOn(s, $"Fingerprint({Original})", exception: () => new FileNotFoundException("gone"));

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.SourceMissing, result.Outcome);
        Assert.Equal(SafeSwapRules.SourceMissingMessage, result.Message);
        Assert.Empty(s.Journal.History);
    }

    [Fact]
    public async Task A_file_that_is_opened_during_the_preflight_is_in_use()
    {
        var s = new SwapScenario();
        FailOn(s, $"IsReadOnly({Original})", exception: () => new FileInUseException("a player has it"));

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.InUse, result.Outcome);
        Assert.Equal(SafeSwapRules.InUseMessage, result.Message);
        Assert.Empty(s.Journal.History);
    }

    [Fact]
    public async Task A_volume_whose_free_space_cannot_be_read_is_noted_and_does_not_block()
    {
        var s = new SwapScenario();
        s.Files.FreeBytes = null;

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Committed, result.Outcome);
        Assert.Equal(["Weir could not read the free space on this file's volume."], result.Warnings);
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(PlatformNotSupportedException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(IOException))]
    public async Task Permissions_that_cannot_be_copied_are_a_warning_and_the_swap_still_commits(Type exceptionType)
    {
        var s = new SwapScenario();
        FailOn(s, $"CopyPermissions({Original} -> {Temp})", exception: () => (Exception)Activator.CreateInstance(exceptionType, "no rights here")!);

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Committed, result.Outcome);
        Assert.Equal(["Weir could not give the cleaned file the original's permissions: no rights here"], result.Warnings);
        Assert.Equal("NEW", s.Files.Read(Original));
        Assert.True(Logged(s, LogLevel.Warning, "could not copy the file's permissions"));
    }

    [Fact]
    public async Task A_permissions_failure_of_any_other_kind_stops_the_swap_at_that_step()
    {
        var s = new SwapScenario();
        FailOn(s, $"CopyPermissions({Original} -> {Temp})", exception: () => new ArgumentException("odd"));

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Failed, result.Outcome);
        Assert.Equal(SafeSwapRules.FailedMessage("copying the file's permissions", "odd"), result.Message);
        Assert.Equal("OLD", s.Files.Read(Original));
    }

    [Fact]
    public async Task The_cleaned_file_gets_the_configured_ownership_once_it_is_in_place()
    {
        var ownership = new RecordingOwnership();
        var s = new SwapScenario(ownership);
        string? contentWhenApplied = null;
        s.Files.OnStep = operation =>
        {
            if (operation == "Journal(Committed)")
            {
                contentWhenApplied = ownership.Files.Count == 1 ? s.Files.Read(Original) : null;
            }
        };

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Committed, result.Outcome);
        Assert.Equal([Original], ownership.Files);
        Assert.Equal("NEW", contentWhenApplied);
    }

    [Fact]
    public async Task A_swap_that_is_abandoned_applies_no_ownership()
    {
        var ownership = new RecordingOwnership();
        var s = new SwapScenario(ownership);
        s.Validator.Answer = SwapValidation.Fail("too short");

        await s.RunAsync();

        Assert.Empty(ownership.Files);
    }

    [Fact]
    public async Task An_original_that_changes_while_the_work_is_done_is_logged_as_abandoned()
    {
        var s = new SwapScenario();
        s.Validator.During = () => s.Files.Write(Original, "OLD, appended to");

        await s.RunAsync();

        Assert.True(Logged(s, LogLevel.Warning, "the original changed while Weir worked"));
    }

    [Fact]
    public async Task An_original_that_changes_as_it_is_moved_aside_is_logged_as_abandoned()
    {
        var s = new SwapScenario();
        s.Files.OnStep = operation =>
        {
            if (operation == $"Fingerprint({Backup})")
            {
                s.Files.Write(Backup, "OLD plus bytes a writer added");
            }
        };

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.SourceChanged, result.Outcome);
        Assert.True(Logged(s, LogLevel.Warning, "the original changed as Weir moved it aside"));
    }

    [Fact]
    public async Task A_swap_that_cannot_write_its_journal_never_starts_and_logs_why()
    {
        var s = new SwapScenario();
        FailOn(s, "Journal(Writing)");

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Failed, result.Outcome);
        Assert.Empty(s.WrittenTo);
        Assert.True(Logged(s, LogLevel.Warning, "Library swap not started"));
    }

    [Fact]
    public async Task A_rollback_that_cannot_record_itself_is_logged_and_the_original_still_stands()
    {
        var s = new SwapScenario();
        s.Validator.Answer = SwapValidation.Fail("too short");
        FailOn(s, "Journal(RolledBack)");

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.ValidationFailed, result.Outcome);
        Assert.Equal("OLD", s.Files.Read(Original));
        Assert.Equal(SwapJournalState.Writing, s.Journal.Latest(SwapScenario.JobId)!.State);
        Assert.True(Logged(s, LogLevel.Warning, "rolled back but not recorded"));
    }

    [Fact]
    public async Task A_rollback_that_leaves_a_file_behind_leaves_the_journal_for_the_sweep()
    {
        var s = new SwapScenario();
        s.Validator.Answer = SwapValidation.Fail("too short");
        s.Files.LockedForRename.Add(Temp);

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.ValidationFailed, result.Outcome);
        Assert.Equal("OLD", s.Files.Read(Original));
        Assert.True(s.Files.Has(Temp));
        Assert.Equal(SwapJournalState.Writing, s.Journal.Latest(SwapScenario.JobId)!.State);
        Assert.DoesNotContain(s.Journal.History, entry => entry.State == SwapJournalState.RolledBack);
        Assert.True(Logged(s, LogLevel.Warning, "rollback left files for the startup sweep"));
    }

    [Fact]
    public async Task A_clean_commit_is_logged_and_a_failed_record_of_it_is_a_warning_with_the_swap_standing()
    {
        var s = new SwapScenario();
        FailOn(s, "Journal(Committed)");

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Committed, result.Outcome);
        Assert.Equal(["Weir replaced the file but could not record it on the job: the disk said no"], result.Warnings);
        Assert.True(Logged(s, LogLevel.Information, "Library swap committed job_id"));
        Assert.True(Logged(s, LogLevel.Warning, "committed but not recorded"));
    }

    [Fact]
    public async Task A_finished_swap_that_cannot_record_itself_logs_it_and_stays_committed()
    {
        var s = new SwapScenario();
        FailOn(s, "Journal(Finished)");

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Committed, result.Outcome);
        Assert.True(result.BackupRemoved);
        Assert.Empty(result.Warnings);
        Assert.True(Logged(s, LogLevel.Warning, "finished but not recorded"));
    }

    [Fact]
    public async Task A_backup_that_cannot_be_deleted_says_where_it_is_and_that_weir_will_try_again()
    {
        var s = new SwapScenario();
        s.Files.LockedForRename.Add(Backup);

        var result = await s.RunAsync();

        Assert.Equal([$"Weir replaced the file but could not delete the backup copy ({Backup}); it will try again when it next starts."], result.Warnings);
        Assert.True(Logged(s, LogLevel.Warning, "left its backup for the startup sweep"));
    }

    [Fact]
    public async Task An_original_that_cannot_be_moved_to_its_kept_place_says_so_and_waits_for_the_sweep()
    {
        var s = new SwapScenario();
        var keep = new KeepOriginalOptions([SwapScenario.Library], null);
        var destination = OriginalsPathPlanner.DestinationPath(SwapScenario.Library, null, Original);
        s.Files.OnStep = operation =>
        {
            if (operation.StartsWith("ReplaceReservation(", StringComparison.Ordinal))
            {
                throw new IOException("the originals folder is full");
            }
        };

        var result = await s.RunAsync(new SwapOptions(KeepOriginal: keep));

        Assert.Equal(SwapOutcome.Committed, result.Outcome);
        Assert.False(result.BackupRemoved);
        Assert.Null(result.KeptOriginalPath);
        Assert.Equal([SafeSwapRules.OriginalKeepFailedMessage(destination, "the originals folder is full")], result.Warnings);
        Assert.True(Logged(s, LogLevel.Warning, "left its original for the startup sweep to move"));
        Assert.Equal("NEW", s.Files.Read(Original));
        Assert.True(s.Files.Has(Backup));
    }

    [Fact]
    public async Task A_swap_that_is_rolled_back_after_reserving_the_originals_name_releases_it()
    {
        var s = new SwapScenario();
        var keep = new KeepOriginalOptions([SwapScenario.Library], null);
        var destination = OriginalsPathPlanner.DestinationPath(SwapScenario.Library, null, Original);
        FailOn(s, $"Move({Temp} -> {Original})");

        var result = await s.RunAsync(new SwapOptions(KeepOriginal: keep));

        Assert.Equal(SwapOutcome.Failed, result.Outcome);
        Assert.False(s.Files.Has(destination));
        Assert.Equal([Original], s.Files.Paths);
    }
}
