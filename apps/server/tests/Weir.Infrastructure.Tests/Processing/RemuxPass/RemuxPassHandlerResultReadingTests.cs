using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>How the handler reads what a pass reported: what is missing or the wrong kind of value is read as nothing, never as a failure.</summary>
public sealed class RemuxPassHandlerResultReadingTests
{
    private static readonly WireObject Empty = new();

    [Fact]
    public void A_pass_found_its_source_gone_only_when_its_outcome_says_so()
    {
        Assert.True(RemuxPassHandler.IsSourceGone(new WireObject().Set("outcome", RemuxPassOutcomes.SourceGone)));
        Assert.False(RemuxPassHandler.IsSourceGone(new WireObject().Set("outcome", RemuxPassOutcomes.SkippedGuardrail)));
        Assert.False(RemuxPassHandler.IsSourceGone(new WireObject().Set("outcome", 7)));
        Assert.False(RemuxPassHandler.IsSourceGone(Empty));
    }

    [Fact]
    public void The_looks_a_gone_file_has_had_are_counted_from_a_number_and_are_none_when_there_is_none()
    {
        Assert.Equal(2, RemuxPassHandler.GoneLooksIn(new WireObject().Set(GoneLooks.Key, 2)));
        Assert.Equal(0, RemuxPassHandler.GoneLooksIn(new WireObject().Set(GoneLooks.Key, 0)));
        Assert.Equal(0, RemuxPassHandler.GoneLooksIn(new WireObject().Set(GoneLooks.Key, "2")));
        Assert.Equal(0, RemuxPassHandler.GoneLooksIn(Empty));
    }

    [Theory]
    [InlineData(false, 0, true)]
    [InlineData(false, 1, false)]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, false)]
    public void A_gone_file_is_entered_in_Activity_once_by_the_first_look_that_finds_it(bool listedWhenClaimed, int looks, bool entered)
    {
        var gone = new WireObject().Set("outcome", RemuxPassOutcomes.SourceGone).Set(GoneLooks.Key, looks);

        Assert.Equal(entered, RemuxPassHandler.AddsGoneEntry(gone, listedWhenClaimed));
    }

    [Fact]
    public void A_result_that_is_not_a_gone_file_is_always_entered_and_one_that_never_counted_looks_is_the_first()
    {
        Assert.True(RemuxPassHandler.AddsGoneEntry(Empty, listedWhenClaimed: true));
        Assert.True(RemuxPassHandler.AddsGoneEntry(new WireObject().Set("outcome", RemuxPassOutcomes.SkippedGuardrail), listedWhenClaimed: true));
        Assert.True(RemuxPassHandler.AddsGoneEntry(new WireObject().Set("outcome", RemuxPassOutcomes.SourceGone), listedWhenClaimed: false));
    }

    [Fact]
    public void A_text_the_result_holds_is_read_and_anything_else_is_the_fallback()
    {
        var result = new WireObject().Set("path", "a/b.mkv").Set("number", 3);

        Assert.Equal("a/b.mkv", RemuxPassHandler.TextIn(result, "path", "none"));
        Assert.Equal("none", RemuxPassHandler.TextIn(result, "number", "none"));
        Assert.Equal("none", RemuxPassHandler.TextIn(result, "missing", "none"));
    }

    [Fact]
    public void The_kind_of_guardrail_is_read_only_from_a_text_that_says_something()
    {
        Assert.Equal("under_minimum_size", RemuxPassHandler.SkipKindOf(new WireObject().Set("skip_kind", "under_minimum_size")));
        Assert.Null(RemuxPassHandler.SkipKindOf(new WireObject().Set("skip_kind", string.Empty)));
        Assert.Null(RemuxPassHandler.SkipKindOf(new WireObject().Set("skip_kind", 4)));
        Assert.Null(RemuxPassHandler.SkipKindOf(Empty));
    }

    [Fact]
    public void A_reason_is_the_text_the_result_gives_and_the_fallback_when_it_gives_none()
    {
        Assert.Equal("because", RemuxPassHandler.TextOr(WireValue.Of("because"), "fallback"));
        Assert.Equal("fallback", RemuxPassHandler.TextOr(WireValue.Of(string.Empty), "fallback"));
        Assert.Equal("fallback", RemuxPassHandler.TextOr(WireNull.Instance, "fallback"));
        Assert.Equal("fallback", RemuxPassHandler.TextOr(null, "fallback"));
    }

    [Fact]
    public void A_file_left_in_place_says_why_a_linked_manager_is_the_reason_only_when_the_workflow_asked_for_deletion()
    {
        var linked = new WorkflowManagerLinks(["deluno"]);

        Assert.Equal(WeirOnlyRejection.LeftInPlace, RemuxPassHandler.WeirOnlyLeftInPlace(chosenDelete: false, linked));
        Assert.Equal(WeirOnlyRejection.LeftInPlace, RemuxPassHandler.WeirOnlyLeftInPlace(chosenDelete: true, WorkflowManagerLinks.None));
        Assert.Equal($"{WeirOnlyRejection.LeftInPlace} {linked.KeptOriginalReason}", RemuxPassHandler.WeirOnlyLeftInPlace(chosenDelete: true, linked));
    }
}
