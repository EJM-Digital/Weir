using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>How a pass reads what the runner reported, including when it did not say: a missing word, a missing count, a record of another shape.</summary>
public sealed partial class RemuxPassHandlerTests
{
    [Fact]
    public void An_outcome_is_the_word_the_pass_reported_or_nothing()
    {
        Assert.Null(RemuxPassHandler.OutcomeOf(new WireObject()));
        Assert.Null(RemuxPassHandler.OutcomeOf(new WireObject().Set("outcome", 3)));
        Assert.Equal(RemuxPassOutcomes.SourceGone, RemuxPassHandler.OutcomeOf(new WireObject().Set("outcome", RemuxPassOutcomes.SourceGone)));
    }

    [Fact]
    public void The_looks_made_at_a_gone_file_are_the_count_the_record_holds_or_nothing()
    {
        Assert.Null(RemuxPassHandler.GoneLooksIn(new WireObject()));
        Assert.Null(RemuxPassHandler.GoneLooksIn(new WireObject().Set(GoneLooks.Key, "once")));
        Assert.Equal(2L, RemuxPassHandler.GoneLooksIn(new WireObject().Set(GoneLooks.Key, 2)));
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData(RemuxPassOutcomes.LiveOutputWritten, true, true)]
    [InlineData(RemuxPassOutcomes.SourceGone, true, false)]
    [InlineData(RemuxPassOutcomes.SourceGone, false, true)]
    public void A_look_adds_a_gone_entry_unless_the_file_was_already_listed_as_gone(string? outcome, bool listedWhenClaimed, bool adds)
    {
        var result = outcome is null ? new WireObject() : new WireObject().Set("outcome", outcome).Set(GoneLooks.Key, 0);

        Assert.Equal(adds, RemuxPassHandler.AddsGoneEntry(result, listedWhenClaimed));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void The_final_look_at_a_gone_file_adds_no_entry_of_its_own(int? looks, bool adds)
    {
        var result = new WireObject().Set("outcome", RemuxPassOutcomes.SourceGone);
        if (looks is { } made)
        {
            result.Set(GoneLooks.Key, made);
        }

        Assert.Equal(adds, RemuxPassHandler.AddsGoneEntry(result, listedWhenClaimed: false));
    }

    [Fact]
    public void The_kind_of_skip_is_the_word_the_pass_gave_or_nothing()
    {
        Assert.Null(RemuxPassHandler.SkipKindOf(new WireObject()));
        Assert.Null(RemuxPassHandler.SkipKindOf(new WireObject().Set("skip_kind", string.Empty)));
        Assert.Null(RemuxPassHandler.SkipKindOf(new WireObject().Set("skip_kind", 4)));
        Assert.Equal("under_minimum", RemuxPassHandler.SkipKindOf(new WireObject().Set("skip_kind", "under_minimum")));
    }

    [Fact]
    public void A_reason_is_the_text_given_or_the_fallback_when_there_is_none()
    {
        Assert.Equal("fallback", RemuxPassHandler.TextOr(null, "fallback"));
        Assert.Equal("fallback", RemuxPassHandler.TextOr(new WireString(string.Empty), "fallback"));
        Assert.Equal("because", RemuxPassHandler.TextOr(new WireString("because"), "fallback"));
    }

    [Theory]
    [InlineData(true, "deluno", true)]
    [InlineData(true, null, false)]
    [InlineData(false, "deluno", false)]
    [InlineData(false, null, false)]
    public void A_file_the_workflow_would_delete_is_said_to_be_kept_only_where_a_manager_keeps_originals(bool chosenDelete, string? linkedKind, bool explained)
    {
        var links = linkedKind is null ? WorkflowManagerLinks.None : new WorkflowManagerLinks([linkedKind]);

        var text = RemuxPassHandler.WeirOnlyLeftInPlace(chosenDelete, links);

        Assert.Equal(explained, text.Contains(new WorkflowManagerLinks(["deluno"]).KeptOriginalReason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_rules_of_a_scope_are_its_workflows_profile_and_otherwise_the_shipped_defaults()
    {
        await _fixture.Store.Execute("DELETE FROM libraries");
        var none = await _fixture.Db(uow => Handler().LoadScopeRulesConfigAsync(uow, "movie"));
        await LibraryAsync();
        await _fixture.Store.Execute("UPDATE libraries SET rule_set_id = NULL");
        var plain = await _fixture.Db(uow => Handler().LoadScopeRulesConfigAsync(uow, "movie"));
        await _fixture.Store.Execute("INSERT INTO rule_sets (name) VALUES ('Profile')");
        await _fixture.Store.Execute("UPDATE libraries SET rule_set_id = (SELECT id FROM rule_sets WHERE name = 'Profile')");
        var profiled = await _fixture.Db(uow => Handler().LoadScopeRulesConfigAsync(uow, "movie"));

        Assert.NotNull(none);
        Assert.NotNull(plain);
        Assert.NotNull(profiled);
    }

    [Fact]
    public async Task A_hand_off_looked_for_when_no_workflow_exists_is_not_found()
    {
        await _fixture.Store.Execute("DELETE FROM libraries");

        Assert.Null(await Handler().CarriedOriginAsync(1, 9999, "Film/film.mkv", "movie", CancellationToken.None));
    }

    [Fact]
    public async Task A_downloaded_scan_for_a_pass_that_named_no_file_still_runs_and_never_fails_the_pass()
    {
        var library = await LibraryAsync();
        var result = new WireObject()
            .Set("ok", true).Set("outcome", RemuxPassOutcomes.LiveOutputWritten).Set("output_file", _folders.Out("film.mkv")).Set("library_id", library);

        var thrown = await Record.ExceptionAsync(() => Handler().DownloadedScanAsync(result, "movie", null));

        Assert.Null(thrown);
    }

    [Theory]
    [InlineData(true, "inspected.mkv")]
    [InlineData(false, null)]
    public async Task A_file_still_gone_after_the_second_look_settles_as_gone_and_keeps_the_path_it_was_looked_for_at(bool inspected, string? expected)
    {
        var request = new RemuxPassRequest { Runtime = _folders.Runtime(), RelativeMediaPath = "gone.mkv", LibraryId = 1 };
        var failed = new WireObject().Set("ok", false);
        if (inspected)
        {
            failed.Set("inspected_source_path", "inspected.mkv");
        }

        var settled = await Handler(goneLookAgain: _ => Task.CompletedTask).SettleVanishedAsync(request, failed, CancellationToken.None);

        Assert.Equal(RemuxPassOutcomes.SourceGone, RemuxPassHandler.OutcomeOf(settled));
        Assert.Equal(expected, settled.Get("inspected_source_path") is WireString path ? path.Value : null);
    }

    [Theory]
    [InlineData(true, RemuxPassOutcomes.SourceGone, null, false)]
    [InlineData(true, RemuxPassOutcomes.LiveOutputWritten, 0, false)]
    [InlineData(true, RemuxPassOutcomes.SourceGone, 1, false)]
    [InlineData(true, RemuxPassOutcomes.SourceGone, 0, true)]
    [InlineData(false, RemuxPassOutcomes.SourceGone, 0, false)]
    public async Task The_first_look_at_a_gone_file_books_the_second_and_no_other_look_does(bool queuesLooks, string outcome, int? looks, bool books)
    {
        var library = await LibraryAsync();
        var result = new WireObject().Set("outcome", outcome);
        if (looks is { } made)
        {
            result.Set(GoneLooks.Key, made);
        }

        await Handler(queuesLooks: queuesLooks).LookAgainForGoneFileAsync(
            9, new WireObject(), null, result, (_folders.Watched, "gone.mkv", "movie", library), CancellationToken.None);

        Assert.Equal(books ? 1 : 0, (await _fixture.Jobs.ListAsync()).Count);
    }

    [Fact]
    public async Task A_second_look_at_a_gone_file_is_booked_without_starting_any_when_the_file_is_not_back_or_the_workflow_is_not_known()
    {
        var result = new WireObject().Set("outcome", RemuxPassOutcomes.SourceGone).Set(GoneLooks.Key, 0);

        await Handler().LookAgainForGoneFileAsync(9, new WireObject(), null, result, (_folders.Watched, "gone.mkv", "movie", null), CancellationToken.None);
        await Handler().LookAgainForGoneFileAsync(10, new WireObject(), null, result, (_folders.Watched, "gone.mkv", "movie", 1), CancellationToken.None);

        Assert.Equal(2, (await _fixture.Jobs.ListAsync()).Count);
    }
}
