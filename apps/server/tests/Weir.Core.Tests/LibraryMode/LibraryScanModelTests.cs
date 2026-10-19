using Weir.Core.Json;
using Weir.Core.LibraryMode;

namespace Weir.Core.Tests.LibraryMode;

/// <summary>What a scan keeps for each file, and how it is written into and read back from the scan job's payload.</summary>
public sealed class LibraryScanModelTests
{
    private static readonly LibraryScanFileEntry Full = new(
        "/media/Films/Heat (1995).mkv",
        1_234_567_890,
        1_700_000_000,
        LibraryFileClassification.WouldChange,
        "Remove 2 audio tracks",
        "Two foreign-language dubs",
        2,
        3,
        "radarr",
        "Heat",
        "{\"streams\":[]}",
        900_000,
        7,
        "title-9",
        11,
        5,
        LibraryProblemKind.Seeding,
        2,
        "rules changed");

    private static LibraryScanFileEntry Bare(string path = "/media/a.mkv", LibraryFileClassification classification = LibraryFileClassification.Matches) =>
        new(path, 10, 20, classification, null, null, 0, 0, null, null, null);

    [Fact]
    public void A_file_is_the_same_one_only_when_path_size_and_modified_time_all_agree()
    {
        Assert.True(Full.MatchesFile(Full.Path, Full.SizeBytes, Full.ModifiedTimeUnixSeconds));
        Assert.False(Full.MatchesFile(Full.Path.ToUpperInvariant(), Full.SizeBytes, Full.ModifiedTimeUnixSeconds));
        Assert.False(Full.MatchesFile(Full.Path, Full.SizeBytes + 1, Full.ModifiedTimeUnixSeconds));
        Assert.False(Full.MatchesFile(Full.Path, Full.SizeBytes, Full.ModifiedTimeUnixSeconds + 1));
    }

    [Theory]
    [InlineData(LibraryFileClassification.Matches, "matches")]
    [InlineData(LibraryFileClassification.WouldChange, "would_change")]
    [InlineData(LibraryFileClassification.CannotProcess, "cannot_process")]
    public void Each_classification_has_one_stored_name(LibraryFileClassification classification, string name) =>
        Assert.Equal(name, LibraryScanFileEntry.ClassificationName(classification));

    [Fact]
    public void A_file_entry_survives_being_written_and_read_back()
    {
        var read = LibraryScanFileEntry.FromPyDict(Full.ToPyDict());

        Assert.Equal(Full, read);
    }

    [Fact]
    public void An_entry_with_nothing_optional_is_stored_with_nulls_and_read_back_the_same()
    {
        var bare = Bare(classification: LibraryFileClassification.CannotProcess);
        var stored = bare.ToPyDict();

        Assert.IsType<WireNull>(stored["manager_kind"]);
        Assert.IsType<WireNull>(stored["problem_kind"]);
        Assert.IsType<WireNull>(stored["link_count"]);
        Assert.Equal(bare, LibraryScanFileEntry.FromPyDict(stored));
    }

    [Fact]
    public void A_problem_is_stored_by_its_wire_name()
    {
        var stored = Full.ToPyDict();

        Assert.Equal("seeding", Assert.IsType<WireString>(stored["problem_kind"]).Value);
        Assert.Equal("would_change", Assert.IsType<WireString>(stored["classification"]).Value);
    }

    [Fact]
    public void Something_that_is_not_an_object_with_a_path_is_not_an_entry()
    {
        Assert.Null(LibraryScanFileEntry.FromPyDict(new WireString("path")));
        Assert.Null(LibraryScanFileEntry.FromPyDict(new WireObject()));
        Assert.Null(LibraryScanFileEntry.FromPyDict(new WireObject().Set("path", 4L)));
        Assert.Null(LibraryScanFileEntry.FromPyDict(new WireObject().Set("path", string.Empty)));
    }

    [Fact]
    public void Missing_or_mistyped_fields_read_as_zero_nothing_or_cannot_process()
    {
        var odd = new WireObject()
            .Set("path", "/media/odd.mkv")
            .Set("size_bytes", "big")
            .Set("classification", 3L)
            .Set("summary", 4L)
            .Set("manager_file_id", "x")
            .Set("problem_kind", "not-a-problem");

        var read = LibraryScanFileEntry.FromPyDict(odd);

        Assert.NotNull(read);
        Assert.Equal(0, read.SizeBytes);
        Assert.Equal(0, read.ModifiedTimeUnixSeconds);
        Assert.Equal(LibraryFileClassification.CannotProcess, read.Classification);
        Assert.Null(read.Summary);
        Assert.Null(read.ManagerFileId);
        Assert.Null(read.ProblemKind);
        Assert.Null(read.LinkCount);
    }

    [Fact]
    public void An_unknown_classification_name_reads_as_cannot_process()
    {
        var odd = new WireObject().Set("path", "/media/odd.mkv").Set("classification", "somebody_else");

        Assert.Equal(LibraryFileClassification.CannotProcess, LibraryScanFileEntry.FromPyDict(odd)!.Classification);
    }

    [Fact]
    public void A_snapshot_is_stored_by_the_second_and_read_back_with_its_files_and_errors()
    {
        var generated = new DateTimeOffset(2026, 10, 19, 6, 30, 0, TimeSpan.Zero);
        var snapshot = new LibraryScanSnapshot(4, generated, [Full, Bare()], ["Could not read /media/x.mkv"]);
        var payload = new WireObject().Set(LibraryScanSnapshot.PayloadKey, snapshot.ToPyDict());

        var read = LibraryScanSnapshot.FromPayload(payload, 4);

        Assert.NotNull(read);
        Assert.Equal(4, read.LibraryId);
        Assert.Equal(generated, read.GeneratedAt);
        Assert.Equal([Full, Bare()], read.Files);
        Assert.Equal(["Could not read /media/x.mkv"], read.Errors);
    }

    [Fact]
    public void A_payload_with_no_scan_has_no_snapshot()
    {
        Assert.Null(LibraryScanSnapshot.FromPayload(new WireObject(), 4));
        Assert.Null(LibraryScanSnapshot.FromPayload(new WireObject().Set(LibraryScanSnapshot.PayloadKey, "done"), 4));
    }

    [Fact]
    public void A_scan_with_missing_parts_reads_as_empty_and_dated_at_the_epoch_and_skips_what_is_not_an_entry()
    {
        var empty = LibraryScanSnapshot.FromPayload(new WireObject().Set(LibraryScanSnapshot.PayloadKey, new WireObject()), 9);

        Assert.NotNull(empty);
        Assert.Equal(DateTimeOffset.UnixEpoch, empty.GeneratedAt);
        Assert.Empty(empty.Files);
        Assert.Empty(empty.Errors);

        var mixed = new WireObject()
            .Set("files", new WireArray([Bare().ToPyDict(), new WireString("junk"), new WireObject()]))
            .Set("errors", new WireArray([new WireString("one"), new WireInteger(2)]));
        var read = LibraryScanSnapshot.FromPayload(new WireObject().Set(LibraryScanSnapshot.PayloadKey, mixed), 9);

        Assert.NotNull(read);
        Assert.Equal([Bare()], read.Files);
        Assert.Equal(["one"], read.Errors);
    }
}
