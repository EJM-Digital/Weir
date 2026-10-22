using Weir.Infrastructure.LibraryMode;

namespace Weir.Infrastructure.Tests.LibraryMode;

public sealed class OriginalsMoverTests
{
    private const string Source = "/lib/.weir-originals/film.mkv";

    [Fact]
    public void Reserve_claims_the_first_free_name()
    {
        var files = new FakeSwapFileSystem();

        var reserved = OriginalsMover.Reserve(files, Source);

        Assert.Equal(Source, reserved);
        Assert.True(files.Has(Source));
        Assert.Equal(string.Empty, files.Read(Source));
    }

    [Fact]
    public void Reserve_moves_to_the_next_suffix_when_a_name_is_taken()
    {
        var files = new FakeSwapFileSystem();
        files.Put(Source, "something already there");

        var reserved = OriginalsMover.Reserve(files, Source);

        Assert.Equal("/lib/.weir-originals/film (2).mkv", reserved);
    }

    [Fact]
    public void Two_reservations_for_the_same_name_never_collide()
    {
        // What a plain existence-then-write check cannot guarantee: both calls see the base name "taken" by the
        // time they act, because TryReserve is the check and the claim in one atomic step.
        var files = new FakeSwapFileSystem();

        var first = OriginalsMover.Reserve(files, Source);
        var second = OriginalsMover.Reserve(files, Source);

        Assert.NotEqual(first, second);
        Assert.Equal(Source, first);
        Assert.Equal("/lib/.weir-originals/film (2).mkv", second);
        Assert.True(files.Has(first));
        Assert.True(files.Has(second));
    }

    [Fact]
    public void Reserve_tries_exactly_the_number_of_names_it_is_allowed()
    {
        var files = new FakeSwapFileSystem();
        files.Put(Source, "taken");

        var reserved = OriginalsMover.Reserve(files, Source, limit: 2);

        Assert.Equal("/lib/.weir-originals/film (2).mkv", reserved);
    }

    [Fact]
    public void Reserve_fails_naming_the_folder_and_the_number_of_tries_when_every_name_is_taken()
    {
        var files = new FakeSwapFileSystem();
        files.Put(Source, "taken");
        files.Put("/lib/.weir-originals/film (2).mkv", "taken");

        var error = Assert.Throws<IOException>(() => OriginalsMover.Reserve(files, Source, limit: 2));

        Assert.Equal($"Weir could not find a free name for the kept original near '{Source}' after 2 attempts.", error.Message);
    }

    [Fact]
    public void Reserve_makes_the_folder_first_and_a_bare_file_name_has_none_to_make()
    {
        var files = new FakeSwapFileSystem();

        OriginalsMover.Reserve(files, Source);
        OriginalsMover.Reserve(files, "film.mkv");

        Assert.Equal(["EnsureDirectory(/lib/.weir-originals)", $"TryReserve({Source})", "TryReserve(film.mkv)"], files.Operations);
    }

    [Fact]
    public void Fill_replaces_the_reservation_on_the_same_volume()
    {
        var files = new FakeSwapFileSystem();
        files.Put("/lib/backup.mkv", "OLD");
        var reserved = OriginalsMover.Reserve(files, Source);

        OriginalsMover.Fill(files, "/lib/backup.mkv", reserved);

        Assert.Equal("OLD", files.Read(reserved));
        Assert.False(files.Has("/lib/backup.mkv"));
        Assert.Contains(files.Operations, op => op.StartsWith("ReplaceReservation(", StringComparison.Ordinal));
        Assert.DoesNotContain(files.Operations, op => op.StartsWith("CopyOverReservation(", StringComparison.Ordinal));
    }

    [Fact]
    public void Fill_copies_and_verifies_across_volumes_then_deletes_the_source()
    {
        var files = new FakeSwapFileSystem { OtherVolumeFolder = "/originals-volume" };
        files.Put("/lib/backup.mkv", "OLD");
        var reserved = OriginalsMover.Reserve(files, "/originals-volume/film.mkv");

        OriginalsMover.Fill(files, "/lib/backup.mkv", reserved);

        Assert.Equal("OLD", files.Read(reserved));
        Assert.False(files.Has("/lib/backup.mkv"));
    }

    [Fact]
    public void Fill_discards_a_corrupt_cross_volume_copy_and_keeps_the_source()
    {
        var files = new FakeSwapFileSystem { OtherVolumeFolder = "/originals-volume", CorruptNextCopy = true };
        files.Put("/lib/backup.mkv", "OLD");
        var reserved = OriginalsMover.Reserve(files, "/originals-volume/film.mkv");

        var error = Assert.Throws<IOException>(() => OriginalsMover.Fill(files, "/lib/backup.mkv", reserved));

        Assert.Contains("did not match", error.Message, StringComparison.Ordinal);
        Assert.True(files.Has("/lib/backup.mkv"), "the source is never deleted when the copy fails verification");
        Assert.False(files.Has(reserved), "a copy that failed verification is not left behind");
    }

    [Fact]
    public void Recover_fills_a_reservation_that_was_never_completed()
    {
        var files = new FakeSwapFileSystem();
        files.Put("/lib/backup.mkv", "OLD");
        var reserved = OriginalsMover.Reserve(files, Source);

        OriginalsMover.Recover(files, "/lib/backup.mkv", reserved);

        Assert.Equal("OLD", files.Read(reserved));
        Assert.False(files.Has("/lib/backup.mkv"));
    }

    [Fact]
    public void Recover_only_deletes_the_backup_when_the_fill_already_finished()
    {
        var files = new FakeSwapFileSystem();
        files.Put("/lib/backup.mkv", "OLD");
        files.Put(Source, "OLD");

        OriginalsMover.Recover(files, "/lib/backup.mkv", Source);

        Assert.False(files.Has("/lib/backup.mkv"));
        Assert.Equal("OLD", files.Read(Source));
        Assert.DoesNotContain(files.Operations, op => op.StartsWith("ReplaceReservation(", StringComparison.Ordinal) || op.StartsWith("CopyOverReservation(", StringComparison.Ordinal));
    }

    [Fact]
    public void Recover_refuses_to_guess_at_a_genuine_mismatch()
    {
        var files = new FakeSwapFileSystem();
        files.Put("/lib/backup.mkv", "OLD");
        files.Put(Source, "SOMETHING ELSE ENTIRELY");

        var error = Assert.Throws<OriginalsKeepConflictException>(() => OriginalsMover.Recover(files, "/lib/backup.mkv", Source));

        Assert.Contains("does not match", error.Message, StringComparison.Ordinal);
        Assert.True(files.Has("/lib/backup.mkv"));
        Assert.Equal("SOMETHING ELSE ENTIRELY", files.Read(Source));
    }

    [Fact]
    public void Recover_runs_the_whole_fill_when_the_reservation_itself_did_not_survive()
    {
        var files = new FakeSwapFileSystem();
        files.Put("/lib/backup.mkv", "OLD");

        OriginalsMover.Recover(files, "/lib/backup.mkv", Source);

        Assert.Equal("OLD", files.Read(Source));
        Assert.False(files.Has("/lib/backup.mkv"));
    }

    [Fact]
    public void CleanUpUnfilledReservation_removes_only_an_empty_placeholder()
    {
        var files = new FakeSwapFileSystem();
        OriginalsMover.Reserve(files, Source);

        OriginalsMover.CleanUpUnfilledReservation(files, Source);

        Assert.False(files.Has(Source));
    }

    [Fact]
    public void CleanUpUnfilledReservation_never_touches_a_reservation_that_was_filled()
    {
        var files = new FakeSwapFileSystem();
        files.Put(Source, "OLD");

        OriginalsMover.CleanUpUnfilledReservation(files, Source);

        Assert.True(files.Has(Source));
        Assert.Equal("OLD", files.Read(Source));
    }

    [Fact]
    public void CleanUpUnfilledReservation_is_a_no_op_when_nothing_is_there()
    {
        var files = new FakeSwapFileSystem();

        OriginalsMover.CleanUpUnfilledReservation(files, Source);

        Assert.False(files.Has(Source));
    }
}
