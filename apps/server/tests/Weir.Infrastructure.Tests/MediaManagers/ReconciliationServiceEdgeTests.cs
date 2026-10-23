using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>The checks and repairs for a work folder at its edges: no workflows, a request that is not a repair, too many leftovers, and a leftover Weir cannot remove.</summary>
public sealed class ReconciliationServiceEdgeTests : IDisposable
{
    private const string Remove = "remove_processing_temp_artifact";

    private readonly MediaManagerFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<string> WorkFolderAsync()
    {
        var work = _fixture.Store.Home.Join("work");
        Directory.CreateDirectory(work);
        await _fixture.Db(uow => uow.ExecuteAsync("UPDATE libraries SET work_folder = $w WHERE media_type = 'movie'", ("$w", work)));
        return work;
    }

    private Task<WireObject> RepairAsync(string action, string? path, bool confirm = true) =>
        _fixture.Db(uow => ReconciliationService.RepairAsync(uow, action, null, path, confirm));

    [Fact]
    public void A_lifecycle_error_can_be_made_with_or_without_a_cause()
    {
        Assert.NotNull(new FileLifecycleException().Message);
        Assert.Equal("plain", new FileLifecycleException("plain").Message);
        Assert.IsType<IOException>(new FileLifecycleException("wrapped", new IOException()).InnerException);
    }

    [Fact]
    public async Task With_no_workflows_there_is_nothing_to_report()
    {
        await _fixture.Db(uow => uow.ExecuteAsync("DELETE FROM libraries"));

        var report = await _fixture.Db(ReconciliationService.BuildReportAsync);

        Assert.Equal(0, ((WireInteger)report["issue_count"]).Value);
        Assert.True(((WireBool)report["ok"]).Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_repair_without_a_path_is_refused(string? path)
    {
        await WorkFolderAsync();

        var failure = await Assert.ThrowsAsync<WireValueException>(() => RepairAsync(Remove, path));

        Assert.Equal("path is required for this repair action.", failure.Message);
    }

    [Fact]
    public async Task A_repair_Weir_does_not_have_is_refused_by_name()
    {
        var failure = await Assert.ThrowsAsync<WireValueException>(() => RepairAsync("reformat_the_disk", null));

        Assert.Equal("Unknown reconciliation repair action: reformat_the_disk", failure.Message);
    }

    [Fact]
    public async Task A_work_folder_that_cannot_be_listed_is_left_out_of_the_report_and_the_report_still_comes()
    {
        var work = await WorkFolderAsync();
        using var unreadable = UnreadablePath.Create(work);

        var report = await _fixture.Db(ReconciliationService.BuildReportAsync);

        Assert.Equal(0, ((WireInteger)report["issue_count"]).Value);
    }

    [Fact]
    public async Task A_work_folder_that_is_not_a_path_is_skipped_when_looking_for_leftovers()
    {
        await _fixture.Db(uow => uow.ExecuteAsync("UPDATE libraries SET work_folder = $w WHERE media_type = 'movie'", ("$w", "not\0a path")));

        var report = await _fixture.Db(ReconciliationService.BuildReportAsync);

        Assert.DoesNotContain(((WireArray)report["issues"]).Items.Cast<WireObject>(), issue => WireConvert.Str(issue["kind"]) == "partial_temp_artifact");
    }

    [Fact]
    public async Task A_leftover_that_cannot_be_deleted_says_so_and_stays()
    {
        var work = await WorkFolderAsync();
        var stuck = Path.Join(work, ".stuck.partial");
        Directory.CreateDirectory(stuck);

        var failure = await Assert.ThrowsAsync<FileLifecycleException>(() => RepairAsync(Remove, stuck));

        Assert.Contains("Could not remove", failure.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(stuck));
    }

    [Fact]
    public async Task Only_the_first_two_hundred_leftovers_are_reported()
    {
        var work = await WorkFolderAsync();
        for (var number = 0; number < ReconciliationRules.MaxIssuesPerCategory + 25; number++)
        {
            await File.WriteAllTextAsync(Path.Join(work, $"left-{number:D3}.partial"), "x");
        }

        var report = await _fixture.Db(ReconciliationService.BuildReportAsync);

        Assert.Equal(ReconciliationRules.MaxIssuesPerCategory, ((WireInteger)report["issue_count"]).Value);
    }

    [Fact]
    public async Task A_folder_set_but_missing_is_named_with_its_role()
    {
        await _fixture.Db(uow => uow.ExecuteAsync(
            "UPDATE libraries SET watched_folder = $gone WHERE media_type = 'movie'", ("$gone", _fixture.Store.Home.Join("gone"))));

        var report = await _fixture.Db(ReconciliationService.BuildReportAsync);

        var issue = Assert.IsType<WireObject>(((WireArray)report["issues"]).Items[0]);
        Assert.Equal("configured_folder_missing", ((WireString)issue["kind"]).Value);
        Assert.Contains("watched folder is configured but is not currently reachable", ((WireString)issue["message"]).Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_inside_the_work_folder_and_a_file_that_is_not_a_leftover_are_not_reported()
    {
        var work = await WorkFolderAsync();
        Directory.CreateDirectory(Path.Join(work, "Film.2024"));
        await File.WriteAllTextAsync(Path.Join(work, "notes.txt"), "x");

        var report = await _fixture.Db(ReconciliationService.BuildReportAsync);

        Assert.Equal(0, ((WireInteger)report["issue_count"]).Value);
    }

    [Fact]
    public async Task A_folder_set_to_a_path_that_is_a_file_is_reachable_as_far_as_the_report_can_tell()
    {
        var file = _fixture.Store.Home.Join("not-a-folder.txt");
        await File.WriteAllTextAsync(file, "x");
        await _fixture.Db(uow => uow.ExecuteAsync("UPDATE libraries SET watched_folder = $file WHERE media_type = 'movie'", ("$file", file)));

        var report = await _fixture.Db(ReconciliationService.BuildReportAsync);

        Assert.DoesNotContain(((WireArray)report["issues"]).Items.Cast<WireObject>(), issue => WireConvert.Str(issue["kind"]) == "configured_folder_missing");
    }
}
