using System.Security.AccessControl;
using System.Security.Principal;
using Weir.Core.Json;
using Weir.Core.LibraryMode;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.Jobs;
using Weir.Infrastructure.Tests.Media;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>The swap and the sweep on real files in a temporary folder, journaled on a real job row.</summary>
public sealed class PhysicalSwapTests : IDisposable
{
    private static readonly DateTime OldTime = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly JobsTestDatabase _db = new();
    private readonly string _library;
    private readonly string _original;
    private readonly long _jobId;
    private readonly ProcessingJobSwapJournal _journal;
    private readonly FakeValidator _validator = new();
    private readonly SafeSwap _swap;

    public PhysicalSwapTests()
    {
        _library = _db.Join("library");
        Directory.CreateDirectory(Path.Join(_library, "Movie (2020)"));
        _original = Path.Join(_library, "Movie (2020)", "Movie (2020).mkv");
        File.WriteAllText(_original, "original content");
        File.SetLastWriteTimeUtc(_original, OldTime);
        _db.Execute(
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status) VALUES ('library:1', 'processing.library.clean.v1', $payload, 'leased')",
            ("$payload", "{\"library_id\":1,\"relative_media_path\":\"Movie (2020)/Movie (2020).mkv\"}"));
        _jobId = (long)_db.Scalar("SELECT id FROM jobs WHERE dedupe_key = 'library:1'")!;
        _journal = new ProcessingJobSwapJournal(_db.Database);
        _swap = new SafeSwap(PhysicalSwapFileSystem.Instance, _journal, _validator, new ListLogger<SafeSwap>());
    }

    private string Temp => SafeSwapRules.TempPath(_original);

    private string Backup => SafeSwapRules.BackupPath(_original);

    public void Dispose()
    {
        if (File.Exists(_original))
        {
            File.SetAttributes(_original, FileAttributes.Normal);
        }

        _db.Dispose();
    }

    private Task<SwapResult> RunAsync(Action<string>? duringWrite = null) =>
        _swap.RunAsync(
            _jobId,
            _original,
            async (temp, token) =>
            {
                await File.WriteAllTextAsync(temp, "cleaned content", token);
                duringWrite?.Invoke(temp);
            });

    private string[] FilesInLibrary() =>
        Directory.GetFiles(_library, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(_library, path)).Order(StringComparer.Ordinal).ToArray();

    private WireObject Payload() => (WireObject)WireJsonParser.Parse((string)_db.Scalar("SELECT payload_json FROM jobs WHERE id = $id", ("$id", _jobId))!);

    private (string State, string OriginalPath, string TempPath, string BackupPath, bool Committed) SwapRow()
    {
        var row = _db.QueryRow("SELECT state, original_path, temp_path, backup_path, committed FROM library_swaps WHERE job_id = $id", ("$id", _jobId))
            ?? throw new InvalidOperationException("No library_swaps row was recorded for this job.");
        return ((string)row[0]!, (string)row[1]!, (string)row[2]!, (string)row[3]!, Convert.ToInt64(row[4]) != 0);
    }

    [Fact]
    public async Task A_real_file_is_replaced_in_place_with_a_new_mtime_and_the_job_row_records_it()
    {
        var result = await RunAsync();

        Assert.Equal(SwapOutcome.Committed, result.Outcome);
        Assert.True(result.BackupRemoved);
        Assert.Equal([Path.Join("Movie (2020)", "Movie (2020).mkv")], FilesInLibrary());
        Assert.Equal("cleaned content", await File.ReadAllTextAsync(_original));
        Assert.True(File.GetLastWriteTimeUtc(_original) > OldTime.AddYears(10), "the manager's rescan needs the mtime to move");

        // The job's own payload is never touched by the swap journal (#557 moved it to library_swaps).
        Assert.Equal(["library_id", "relative_media_path"], Payload().Keys);
        var swap = SwapRow();
        Assert.Equal("finished", swap.State);
        Assert.Equal(_original, swap.OriginalPath);
        Assert.Equal(Temp, swap.TempPath);
        Assert.Equal(Backup, swap.BackupPath);
        Assert.True(swap.Committed);
        Assert.Empty(await _journal.ListUnfinishedAsync());
    }

    [WindowsFact("Share modes that block a rename are a Windows behaviour.")]
    public async Task A_file_held_open_by_another_handle_is_in_use_before_any_work()
    {
        using (new FileStream(_original, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(PhysicalSwapFileSystem.Instance.IsInUse(_original));
            var result = await RunAsync();

            Assert.Equal(SwapOutcome.InUse, result.Outcome);
            Assert.Equal(SafeSwapRules.InUseMessage, result.Message);
        }

        Assert.Equal([Path.Join("Movie (2020)", "Movie (2020).mkv")], FilesInLibrary());
        Assert.Equal("original content", await File.ReadAllTextAsync(_original));
        Assert.False(PhysicalSwapFileSystem.Instance.IsInUse(_original));
    }

    [WindowsFact("Share modes that block a rename are a Windows behaviour.")]
    public async Task A_file_opened_by_a_player_during_the_write_is_in_use_at_the_commit_and_left_intact()
    {
        FileStream? player = null;
        try
        {
            var result = await RunAsync(_ => player = new FileStream(_original, FileMode.Open, FileAccess.Read, FileShare.Read));

            Assert.Equal(SwapOutcome.InUse, result.Outcome);
            Assert.Equal("original content", await File.ReadAllTextAsync(_original));
        }
        finally
        {
            player?.Dispose();
        }

        Assert.Equal([Path.Join("Movie (2020)", "Movie (2020).mkv")], FilesInLibrary());
        var swap = SwapRow();
        Assert.Equal("rolled_back", swap.State);
        Assert.False(swap.Committed);
    }

    [WindowsFact("Share modes are a Windows behaviour.")]
    public async Task A_reader_that_shares_delete_does_not_block_the_swap()
    {
        using var reader = new FileStream(_original, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        Assert.False(PhysicalSwapFileSystem.Instance.IsInUse(_original));
        var result = await RunAsync();

        Assert.Equal(SwapOutcome.Committed, result.Outcome);
        Assert.Equal("cleaned content", await File.ReadAllTextAsync(_original));
    }

    [Fact]
    public async Task A_hardlinked_real_file_is_skipped()
    {
        var link = Path.Join(_db.Join("seeding"), "Movie (2020).mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (!FileLifecycle.CreateHardLink(link, _original))
        {
            return; // the volume does not support hard links
        }

        Assert.Equal(2, PhysicalSwapFileSystem.Instance.LinkCount(_original));
        var result = await RunAsync();

        Assert.Equal(SwapOutcome.Hardlinked, result.Outcome);
        Assert.Equal("original content", await File.ReadAllTextAsync(link));
        Assert.Equal([Path.Join("Movie (2020)", "Movie (2020).mkv")], FilesInLibrary());
        Assert.False(Payload().ContainsKey("library_swap"));

        File.Delete(link);
        Assert.Equal(1, PhysicalSwapFileSystem.Instance.LinkCount(_original));
    }

    [WindowsFact("The read-only attribute is a Windows behaviour.")]
    public async Task A_read_only_real_file_is_refused()
    {
        File.SetAttributes(_original, FileAttributes.ReadOnly);

        var result = await RunAsync();

        Assert.Equal(SwapOutcome.NotWritable, result.Outcome);
        Assert.Equal("original content", await File.ReadAllTextAsync(_original));
    }

    [Fact]
    public async Task A_cleaned_copy_that_fails_validation_is_discarded_and_the_real_original_kept()
    {
        _validator.Answer = SwapValidation.Fail("the output is 40 minutes shorter than the source");
        var copyBesideOriginalWhenChecked = false;
        _validator.During = () => copyBesideOriginalWhenChecked = File.Exists(Temp) && Path.GetDirectoryName(Temp) == Path.GetDirectoryName(_original);

        var result = await RunAsync();

        Assert.True(copyBesideOriginalWhenChecked, "the copy is written as <name>.weir-tmp<ext> beside the original before it is checked");
        Assert.Equal(SwapOutcome.ValidationFailed, result.Outcome);
        Assert.Equal([Temp], _validator.Checked);
        Assert.Equal([Path.Join("Movie (2020)", "Movie (2020).mkv")], FilesInLibrary());
        Assert.Equal("original content", await File.ReadAllTextAsync(_original));
        Assert.False(SwapRow().Committed);
    }

    [Fact]
    public async Task A_real_change_to_the_original_replaces_nothing()
    {
        var result = await RunAsync(_ => File.AppendAllText(_original, " and more"));

        Assert.Equal(SwapOutcome.SourceChanged, result.Outcome);
        Assert.Equal([Path.Join("Movie (2020)", "Movie (2020).mkv")], FilesInLibrary());
        Assert.Equal("original content and more", await File.ReadAllTextAsync(_original));
    }

    [WindowsFact("Access control lists are a Windows behaviour.")]
    public async Task An_explicit_permission_on_the_original_is_carried_to_the_cleaned_file()
    {
        // [WindowsFact] already skips this at runtime on other platforms; this check is what lets the
        // platform-compatibility analyzer (CA1416) see these Windows-only ACL APIs as guarded.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var info = new FileInfo(_original);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.ReadData, AccessControlType.Allow));
        info.SetAccessControl(security);

        var result = await RunAsync();

        Assert.Equal(SwapOutcome.Committed, result.Outcome);
        Assert.Empty(result.Warnings);
        var rules = new FileInfo(_original).GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
        var carried = false;
        foreach (FileSystemAccessRule rule in rules)
        {
            carried |= rule.IdentityReference.Equals(everyone) && rule.FileSystemRights.HasFlag(FileSystemRights.ReadData) && rule.AccessControlType == AccessControlType.Allow;
        }

        Assert.True(carried, "the explicit rule on the original was not carried to the cleaned file");
    }

    [Fact]
    public async Task A_copy_left_by_an_earlier_swap_is_cleared_before_the_next_one_starts()
    {
        File.WriteAllText(Temp, "half a copy");

        var result = await RunAsync();

        Assert.Equal(SwapOutcome.Committed, result.Outcome);
        Assert.Equal([Path.Join("Movie (2020)", "Movie (2020).mkv")], FilesInLibrary());
        Assert.Equal("cleaned content", await File.ReadAllTextAsync(_original));
    }

    [Fact]
    public async Task A_backup_left_beside_an_original_in_place_is_removed_and_never_put_back_over_it()
    {
        File.WriteAllText(Backup, "the original an earlier swap replaced");

        var result = await RunAsync();

        Assert.Equal(SwapOutcome.Committed, result.Outcome);
        Assert.Equal([Path.Join("Movie (2020)", "Movie (2020).mkv")], FilesInLibrary());
        Assert.Equal("cleaned content", await File.ReadAllTextAsync(_original));
    }

    [Fact]
    public async Task A_backup_left_without_its_original_is_put_back_before_the_next_swap_starts()
    {
        File.Move(_original, Backup);
        File.WriteAllText(Temp, "half a copy");
        string? seenByTheWriter = null;

        var result = await RunAsync(_ => seenByTheWriter = File.ReadAllText(_original));

        Assert.Equal(SwapOutcome.Committed, result.Outcome);
        Assert.Equal("original content", seenByTheWriter);
        Assert.Equal([Path.Join("Movie (2020)", "Movie (2020).mkv")], FilesInLibrary());
        Assert.Equal("cleaned content", await File.ReadAllTextAsync(_original));
    }

    [WindowsFact("Share modes that block a delete are a Windows behaviour.")]
    public async Task Leftovers_another_program_holds_stop_the_swap_before_any_work()
    {
        File.WriteAllText(Temp, "half a copy");
        using (new FileStream(Temp, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await AssertSwapStopsBeforeAnyWorkAsync();
        }
    }

    [PosixFact("Folder permissions are a POSIX behaviour.")]
    public async Task Leftovers_in_a_folder_Weir_cannot_change_stop_the_swap_before_any_work()
    {
        // [PosixFact] already skips this at runtime on Windows; this check is what lets the platform-compatibility analyzer
        // (CA1416) see the Unix-only mode calls as guarded.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(Temp, "half a copy");
        var folder = Path.GetDirectoryName(_original)!;
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            if (CanStillWriteIn(folder))
            {
                return; // running as root: the folder's permissions do not hold it back
            }

            await AssertSwapStopsBeforeAnyWorkAsync();
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private async Task AssertSwapStopsBeforeAnyWorkAsync()
    {
        var wrote = false;

        var result = await RunAsync(_ => wrote = true);

        Assert.Equal(SwapOutcome.Failed, result.Outcome);
        Assert.Equal(SafeSwapRules.FailedMessage("clearing up an earlier interrupted swap", "its leftover files could not be removed"), result.Message);
        Assert.False(wrote, "the swap went on to write a copy with the earlier one still in the way");
        Assert.Equal("original content", await File.ReadAllTextAsync(_original));
        Assert.Equal("half a copy", await File.ReadAllTextAsync(Temp));
        Assert.Empty(await _journal.ListUnfinishedAsync());
    }

    private static bool CanStillWriteIn(string folder)
    {
        var probe = Path.Join(folder, ".probe");
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [WindowsFact("Deleting a read-only file is refused on Windows only.")]
    public async Task A_backup_that_cannot_be_deleted_is_left_for_the_sweep_and_the_swap_still_commits()
    {
        // The original turns read-only after the checks, as the swap works: the backup is that file renamed, so it cannot be deleted.
        var result = await RunAsync(_ => File.SetAttributes(_original, FileAttributes.ReadOnly));

        try
        {
            Assert.Equal(SwapOutcome.Committed, result.Outcome);
            Assert.False(result.BackupRemoved);
            Assert.Contains("could not delete the backup copy", Assert.Single(result.Warnings), StringComparison.Ordinal);
            Assert.Equal("cleaned content", await File.ReadAllTextAsync(_original));
            Assert.Equal("original content", await File.ReadAllTextAsync(Backup));
            Assert.Equal(2, FilesInLibrary().Length);
            Assert.Single(await _journal.ListUnfinishedAsync());
        }
        finally
        {
            File.SetAttributes(Backup, FileAttributes.Normal);
        }

        var report = await new SwapRecoverySweep(PhysicalSwapFileSystem.Instance, _journal, new RecordingActivityWriter(), new ListLogger<SwapRecoverySweep>())
            .RunAsync([], walkFolders: false);

        Assert.Equal(new SwapRecoveryReport(0, 1, 0, 0), report);
        Assert.Equal([Path.Join("Movie (2020)", "Movie (2020).mkv")], FilesInLibrary());
        Assert.Equal("cleaned content", await File.ReadAllTextAsync(_original));
        Assert.Equal("recovered", SwapRow().State);
    }

    [Fact]
    public void A_rename_never_replaces_an_existing_file()
    {
        var other = Path.Join(_library, "other.mkv");
        File.WriteAllText(other, "someone else's file");

        Assert.ThrowsAny<IOException>(() => PhysicalSwapFileSystem.Instance.Move(_original, other));

        Assert.Equal("original content", File.ReadAllText(_original));
        Assert.Equal("someone else's file", File.ReadAllText(other));
    }

    [Fact]
    public void Free_space_is_read_for_a_real_folder()
    {
        Assert.True(PhysicalSwapFileSystem.Instance.AvailableFreeBytes(_library) > 0);
    }

    [Fact]
    public void A_missing_folder_fails_the_write_probe()
    {
        Assert.ThrowsAny<IOException>(() => PhysicalSwapFileSystem.Instance.ProbeWrite(Path.Join(_library, "missing")));
    }

    [Fact]
    public async Task The_real_sweep_recovers_every_case_across_nested_folders_and_leaves_other_files_alone()
    {
        // Interrupted after the original was moved aside: backup present, original missing, temp present.
        var season = Path.Join(_library, "Show", "Season 01");
        Directory.CreateDirectory(season);
        var episode = Path.Join(season, "Show.S01E01.mkv");
        File.WriteAllText(SafeSwapRules.BackupPath(episode), "episode original");
        File.WriteAllText(SafeSwapRules.TempPath(episode), "episode cleaned");

        // Interrupted after the commit: original (cleaned) and backup present.
        File.WriteAllText(_original, "cleaned content");
        File.WriteAllText(Backup, "original content");

        // Interrupted while writing: temp only.
        var other = Path.Join(_library, "Other.mp4");
        File.WriteAllText(other, "other original");
        File.WriteAllText(SafeSwapRules.TempPath(other), "half written");

        var decoy = Path.Join(_library, "Other.weir-tmp-notes.txt");
        File.WriteAllText(decoy, "not Weir's");

        var logger = new ListLogger<SwapRecoverySweep>();
        var report = await new SwapRecoverySweep(PhysicalSwapFileSystem.Instance, _journal, new RecordingActivityWriter(), logger).RunAsync([_library], walkFolders: true);

        Assert.Equal(new SwapRecoveryReport(2, 1, 1, 0), report);
        Assert.Equal(
            new[] { "Movie (2020)/Movie (2020).mkv", "Other.mp4", "Other.weir-tmp-notes.txt", "Show/Season 01/Show.S01E01.mkv" }
                .Select(path => path.Replace('/', Path.DirectorySeparatorChar))
                .Order(StringComparer.Ordinal),
            FilesInLibrary());
        Assert.Equal("episode original", await File.ReadAllTextAsync(episode));
        Assert.Equal("cleaned content", await File.ReadAllTextAsync(_original));
        Assert.Equal("other original", await File.ReadAllTextAsync(other));
    }

    [Fact]
    public async Task The_real_sweep_goes_to_journaled_paths_first()
    {
        await _journal.RecordAsync(new SwapJournalEntry(_jobId, _original, SwapJournalState.Committing));
        File.Move(_original, Backup);
        File.WriteAllText(Temp, "cleaned content");

        var report = await new SwapRecoverySweep(PhysicalSwapFileSystem.Instance, _journal, new RecordingActivityWriter(), new ListLogger<SwapRecoverySweep>())
            .RunAsync([], walkFolders: false);

        Assert.Equal(new SwapRecoveryReport(1, 0, 1, 0), report);
        Assert.Equal([Path.Join("Movie (2020)", "Movie (2020).mkv")], FilesInLibrary());
        Assert.Equal("original content", await File.ReadAllTextAsync(_original));
        Assert.Equal("recovered", SwapRow().State);
    }
}
