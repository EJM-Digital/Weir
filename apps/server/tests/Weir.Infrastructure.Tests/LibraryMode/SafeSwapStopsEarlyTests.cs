using Microsoft.Extensions.Logging;
using Weir.Core.LibraryMode;
using Weir.Infrastructure.LibraryMode;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>
/// A swap that stops before the commit: what it still asks of the disk, what it logs, what it says about the backup, and that a
/// cancellation is honoured at once and never lets the swap carry on to the commit.
/// </summary>
public sealed class SafeSwapStopsEarlyTests
{
    private const string Original = SwapScenario.Original;
    private static readonly string Temp = SwapScenario.Temp;
    private static readonly string Backup = SwapScenario.Backup;

    [Fact]
    public void A_missing_file_is_refused_at_the_existence_check_without_reading_it_further()
    {
        var s = new SwapScenario();
        s.Files.Delete(Original);
        s.Files.Operations.Clear();

        var preflight = s.Swap.Preflight(Original);

        Assert.Equal(SwapOutcome.SourceMissing, preflight.Refusal);
        Assert.Equal(SafeSwapRules.SourceMissingMessage, preflight.Message);
        Assert.Equal([$"FileExists({Original})"], s.Files.Operations);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_leftover_check_that_cannot_be_read_stops_the_swap_before_any_work(int operationIndex)
    {
        var s = new SwapScenario();
        s.Files.FaultAt = operationIndex;
        s.Files.Mode = FaultMode.Throw;

        var result = await s.RunAsync();

        Assert.Equal([$"FileExists({Backup})", $"FileExists({Temp})"], s.Files.Operations.Take(2));
        Assert.Equal(SwapOutcome.Failed, result.Outcome);
        Assert.Equal(
            SafeSwapRules.FailedMessage("clearing up an earlier interrupted swap", "its leftover files could not be removed"),
            result.Message);
        Assert.False(result.BackupRemoved);
        Assert.Empty(s.WrittenTo);
        Assert.Empty(s.Journal.History);
        Assert.Equal("OLD", s.Files.Read(Original));
    }

    [Fact]
    public async Task Leftovers_that_cannot_be_removed_do_not_claim_the_backup_was_removed()
    {
        var s = new SwapScenario();
        s.Files.Put(Backup, "an old backup");
        s.Files.LockedForRename.Add(Backup);

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Failed, result.Outcome);
        Assert.False(result.BackupRemoved);
    }

    [Fact]
    public async Task A_refused_preflight_does_not_claim_the_backup_was_removed()
    {
        var s = new SwapScenario();
        s.Files.Put(Original, "OLD", links: 2);

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Hardlinked, result.Outcome);
        Assert.False(result.BackupRemoved);
    }

    [Fact]
    public async Task A_journal_that_cannot_be_written_does_not_claim_the_backup_was_removed()
    {
        var s = new SwapScenario();
        s.Files.OnStep = operation =>
        {
            if (operation == "Journal(Writing)")
            {
                throw new IOException("the journal is locked");
            }
        };

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Failed, result.Outcome);
        Assert.False(result.BackupRemoved);
        Assert.Empty(s.WrittenTo);
    }

    [Fact]
    public async Task An_abandoned_swap_does_not_claim_the_backup_was_removed()
    {
        var s = new SwapScenario();
        s.Validator.Answer = SwapValidation.Fail("the output is shorter than the source");

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.ValidationFailed, result.Outcome);
        Assert.False(result.BackupRemoved);
    }

    [Fact]
    public async Task Putting_the_original_back_after_an_abandoned_swap_is_logged_as_expected_not_as_a_warning()
    {
        var s = new SwapScenario();
        s.Files.OnStep = operation =>
        {
            if (operation == $"Fingerprint({Backup})")
            {
                s.Files.Write(Backup, "OLD plus bytes a writer added through its open handle");
            }
        };

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.SourceChanged, result.Outcome);
        Assert.Contains(s.SwapLogger.Entries, entry => entry.Level == LogLevel.Information && entry.Message.StartsWith("Library swap put the original back", StringComparison.Ordinal));
        Assert.DoesNotContain(s.SwapLogger.Entries, entry => entry.Message.Contains("Weir restored a library file from its backup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cancel_that_arrives_as_the_writer_returns_stops_the_swap_before_the_cleaned_copy_is_checked()
    {
        using var cancel = new CancellationTokenSource();
        var s = new SwapScenario();
        s.Writer = (temp, _) =>
        {
            s.Files.Write(temp, "NEW");
            cancel.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.RunAsync(cancellationToken: cancel.Token));

        Assert.Empty(s.Validator.Checked);
        Assert.Equal([Original], s.Files.Paths);
        Assert.Equal("OLD", s.Files.Read(Original));
        Assert.Equal(SwapJournalState.RolledBack, s.Journal.Latest(SwapScenario.JobId)!.State);
    }

    [Fact]
    public async Task A_cancel_that_arrives_while_the_cleaned_copy_is_checked_never_reaches_the_commit()
    {
        using var cancel = new CancellationTokenSource();
        var s = new SwapScenario();
        s.Validator.During = cancel.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.RunAsync(cancellationToken: cancel.Token));

        Assert.Equal([Original], s.Files.Paths);
        Assert.Equal("OLD", s.Files.Read(Original));
        Assert.DoesNotContain(s.Files.Effects, effect => effect.StartsWith("Move(", StringComparison.Ordinal));
        Assert.Equal(SwapJournalState.RolledBack, s.Journal.Latest(SwapScenario.JobId)!.State);
    }
}
