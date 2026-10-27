using Microsoft.Extensions.Time.Testing;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>The refusals and edges of the small helpers a pass leans on: pacing, gone files, gone looks and the manual plan's wire shape.</summary>
public sealed class RemuxPassSmallGapsTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);

    private static ProcessingJob Job(string status, DateTimeOffset? notBefore, string? payload) =>
        new(1, "key", "processing.file.remux_pass.v1", payload, status, null, null, 0, 3, null, notBefore, 1, 0, Now, Now);

    [Fact]
    public async Task A_second_reject_inside_the_minimum_gap_waits_for_the_rest_of_it()
    {
        var time = new FakeTimeProvider(Now);
        using var pacing = new RejectPacing(time);
        await pacing.WaitAsync(CancellationToken.None);

        var second = pacing.WaitAsync(CancellationToken.None);
        Assert.False(second.IsCompleted);

        time.Advance(RejectSupportRules.MinSecondsBetweenRejects);
        await second;
    }

    [Theory]
    [InlineData(ProcessingJobStatus.Pending, "{\"gone_looks\": 1}", true, true)]
    [InlineData(ProcessingJobStatus.Pending, "{\"gone_looks\": 0}", true, false)]
    [InlineData(ProcessingJobStatus.Pending, "{\"gone_looks\": 1}", false, false)]
    [InlineData(ProcessingJobStatus.Leased, "{\"gone_looks\": 1}", true, false)]
    [InlineData(ProcessingJobStatus.Pending, "[1, 2]", true, false)]
    [InlineData(ProcessingJobStatus.Pending, "{not json", true, false)]
    [InlineData(ProcessingJobStatus.Pending, null, true, false)]
    public void A_pass_is_waiting_only_when_it_is_held_pending_with_a_gone_look(string status, string? payload, bool held, bool expected)
    {
        var job = Job(status, held ? Now.AddMinutes(5) : null, payload);

        Assert.Equal(expected, GoneLooks.IsWaiting(job));
    }

    [Theory]
    [InlineData(1L, "a.mkv", true)]
    [InlineData(2L, "a.mkv", false)]
    [InlineData(1L, "b.mkv", false)]
    public void A_look_names_a_file_only_in_its_own_workflow_and_at_its_own_path(long libraryId, string relativePath, bool expected)
    {
        var job = Job(ProcessingJobStatus.Pending, Now, "{\"library_id\": 1, \"relative_media_path\": \"a.mkv\"}");

        Assert.Equal(expected, GoneLooks.Names(job, libraryId, relativePath));
    }

    [Theory]
    [InlineData("../outside.mkv")]
    [InlineData("")]
    [InlineData("bad\0name.mkv")]
    public void A_path_that_cannot_be_resolved_is_neither_gone_nor_back(string relativePath)
    {
        using var folder = new TempDirectory();

        Assert.False(GoneSources.HasLeft(folder.Path, relativePath));
        Assert.False(GoneSources.IsBack(folder.Path, relativePath));
    }

    [Fact]
    public void A_manual_plan_with_a_keep_entry_that_is_not_an_object_is_refused()
    {
        var plan = new WireObject().Set("keep", new WireArray([new WireInteger(3)]));

        Assert.Null(ManualPlanJson.FromPyJson(plan));
    }

    [Fact]
    public void A_manual_plan_that_marks_nothing_default_or_forced_and_gives_no_order_keeps_the_tracks_in_file_order()
    {
        var plan = new WireObject().Set("keep", new WireArray([new WireObject().Set("index", 1), new WireObject().Set("index", 2).Set("default", false).Set("forced", false)]));

        var choice = ManualPlanJson.FromPyJson(plan)!;

        Assert.Equal([new ManualKeepEntry(1, false, false), new ManualKeepEntry(2, false, false)], choice.Keep);
        Assert.Empty(choice.Order);
    }

    [Fact]
    public void A_manual_plan_keeps_what_it_marks_default_and_forced_and_the_order_it_gives_without_anything_that_is_not_a_number()
    {
        var plan = new WireObject()
            .Set("keep", new WireArray([new WireObject().Set("index", 3).Set("default", true).Set("forced", true)]))
            .Set("order", new WireArray([new WireInteger(3), new WireString("x"), new WireInteger(1)]));

        var choice = ManualPlanJson.FromPyJson(plan)!;

        Assert.Equal([new ManualKeepEntry(3, true, true)], choice.Keep);
        Assert.Equal([3, 1], choice.Order);
    }

    [Fact]
    public void A_manual_plan_keep_entry_without_an_index_is_refused()
    {
        var plan = new WireObject().Set("keep", new WireArray([new WireObject().Set("default", true)]));

        Assert.Null(ManualPlanJson.FromPyJson(plan));
    }

    [Fact]
    public void A_source_fingerprint_survives_the_trip_through_the_payload()
    {
        var fingerprint = new SourceFingerprint(7UL, 99UL, 123456L, 987654321L);

        var read = ManualPlanJson.FingerprintFromPyJson(ManualPlanJson.ToPyDict(fingerprint));

        Assert.Equal(fingerprint, read);
    }
}
