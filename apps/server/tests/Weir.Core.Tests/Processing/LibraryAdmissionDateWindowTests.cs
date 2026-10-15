using Weir.Core.Processing;

namespace Weir.Core.Tests.Processing;

/// <summary>A workflow's four date windows at their edges: a file's time is compared with each limit to the tick.</summary>
public sealed class LibraryAdmissionDateWindowTests
{
    private static readonly DateTimeOffset Edge = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly LibraryAdmissionRules NoRules = new(
        MediaExtensions: new HashSet<string>(), ExcludeMarkers: new HashSet<string>(), IncludePatterns: [], ExcludePatterns: [],
        MinFileSizeMb: 0, MaxFileSizeMb: 0, RejectedFileAction: "leave",
        CreatedAfter: null, CreatedBefore: null, ModifiedAfter: null, ModifiedBefore: null, ExcludeHidden: false, TopLevelOnly: false);

    private static LibraryAdmissionRejection? Rejection(LibraryAdmissionRules rules, DateTimeOffset? created, DateTimeOffset? modified) =>
        LibraryAdmission.Rejection("a.mkv", "a.mkv", new CandidateFileFacts(1024, created, modified), rules);

    [Fact]
    public void A_file_created_before_the_created_after_limit_is_rejected_and_one_created_at_it_is_not()
    {
        var rules = NoRules with { CreatedAfter = Edge };

        Assert.Equal("skipped_before_created_window", Rejection(rules, Edge.AddTicks(-1), null)?.Counter);
        Assert.Null(Rejection(rules, Edge, null));
        Assert.Null(Rejection(rules, Edge.AddTicks(1), null));
    }

    [Fact]
    public void A_file_created_at_the_created_before_limit_is_rejected_and_one_created_just_before_it_is_not()
    {
        var rules = NoRules with { CreatedBefore = Edge };

        Assert.Null(Rejection(rules, Edge.AddTicks(-1), null));
        Assert.Equal("skipped_after_created_window", Rejection(rules, Edge, null)?.Counter);
        Assert.Equal("skipped_after_created_window", Rejection(rules, Edge.AddTicks(1), null)?.Counter);
    }

    [Fact]
    public void A_file_modified_before_the_modified_after_limit_is_rejected_and_one_modified_at_it_is_not()
    {
        var rules = NoRules with { ModifiedAfter = Edge };

        Assert.Equal("skipped_before_modified_window", Rejection(rules, null, Edge.AddTicks(-1))?.Counter);
        Assert.Null(Rejection(rules, null, Edge));
        Assert.Null(Rejection(rules, null, Edge.AddTicks(1)));
    }

    [Fact]
    public void A_file_modified_at_the_modified_before_limit_is_rejected_and_one_modified_just_before_it_is_not()
    {
        var rules = NoRules with { ModifiedBefore = Edge };

        Assert.Null(Rejection(rules, null, Edge.AddTicks(-1)));
        Assert.Equal("skipped_after_modified_window", Rejection(rules, null, Edge)?.Counter);
        Assert.Equal("skipped_after_modified_window", Rejection(rules, null, Edge.AddTicks(1))?.Counter);
    }

    [Fact]
    public void A_window_open_on_one_side_limits_only_that_side()
    {
        var rules = NoRules with { CreatedAfter = Edge.AddDays(-1), CreatedBefore = Edge.AddDays(1) };

        Assert.Null(Rejection(rules, Edge, null));
        Assert.Equal("skipped_before_created_window", Rejection(rules, Edge.AddDays(-2), null)?.Counter);
        Assert.Equal("skipped_after_created_window", Rejection(rules, Edge.AddDays(2), null)?.Counter);
    }

    [Fact]
    public void A_time_the_filesystem_does_not_report_is_never_held_against_the_file()
    {
        var rules = NoRules with { CreatedAfter = Edge, CreatedBefore = Edge, ModifiedAfter = Edge, ModifiedBefore = Edge };

        Assert.Null(Rejection(rules, null, null));
    }

    [Fact]
    public void The_reason_names_the_time_that_was_read_and_which_side_of_the_window_it_fell_on()
    {
        var rules = NoRules with { ModifiedBefore = Edge };

        var rejection = Rejection(rules, null, Edge);

        Assert.Equal(
            "Skipped because its last-modified time (2025-06-01T12:00:00+00:00) is after this workflow's allowed window.",
            rejection?.Reason);
    }

    [Fact]
    public void The_admission_rules_read_the_four_limits_off_the_workflow_as_utc()
    {
        var workflow = new ProcessingLibraryRecord
        {
            Name = "Movies",
            CreatedAfter = Weir.Core.Time.Timestamp.FromUtc(Edge.UtcDateTime),
            ModifiedBefore = Weir.Core.Time.Timestamp.FromUtc(Edge.UtcDateTime.AddHours(1)),
        };

        var rules = LibraryAdmissionRules.For(workflow);

        Assert.Equal(Edge, rules.CreatedAfter);
        Assert.Equal(Edge.AddHours(1), rules.ModifiedBefore);
        Assert.Null(rules.CreatedBefore);
        Assert.Null(rules.ModifiedAfter);
    }
}
