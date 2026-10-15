using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

public sealed class ConnectionRemovalBlockersTests
{
    private const string Label = "Deluno on nas";

    private static BlockedWorkflow Workflow(long id, string name) => new(id, name);

    private static List<OwedReport> Reports(int count) => [.. Enumerable.Range(1, count).Select(number => new OwedReport($"h{number}", $"film{number}.mkv"))];

    [Fact]
    public void Nothing_in_the_way_is_not_a_blocker()
    {
        Assert.False(ConnectionRemovalBlockers.None.Any);
        Assert.True(new ConnectionRemovalBlockers([Workflow(1, "Movies")], 0, []).Any);
        Assert.True(new ConnectionRemovalBlockers([], 1, []).Any);
        Assert.True(new ConnectionRemovalBlockers([], 0, Reports(1)).Any);
    }

    [Theory]
    [InlineData(1, "Movies still comes from Deluno on nas. Nothing was removed. Before removing it, unlink or remove that workflow.")]
    [InlineData(2, "Movies and TV Shows still come from Deluno on nas. Nothing was removed. Before removing it, unlink or remove those workflows.")]
    [InlineData(3, "Movies, TV Shows and Anime still come from Deluno on nas. Nothing was removed. Before removing it, unlink or remove those workflows.")]
    public void Workflows_are_named_and_the_two_steps_offered(int count, string expected)
    {
        var workflows = new[] { Workflow(1, "Movies"), Workflow(2, "TV Shows"), Workflow(3, "Anime") }.Take(count).ToList();

        Assert.Equal(expected, new ConnectionRemovalBlockers(workflows, 0, []).RemovalRefusal(Label));
    }

    [Theory]
    [InlineData(1, "Weir is still working on 1 hand-off from Deluno on nas. Nothing was removed. Before removing it, let the hand-off finish or cancel it.")]
    [InlineData(4, "Weir is still working on 4 hand-offs from Deluno on nas. Nothing was removed. Before removing it, let the hand-offs finish or cancel them.")]
    public void Hand_offs_in_progress_are_counted(int count, string expected) =>
        Assert.Equal(expected, new ConnectionRemovalBlockers([], count, []).RemovalRefusal(Label));

    [Theory]
    [InlineData(1, "Weir still owes Deluno on nas a report on a finished hand-off. Nothing was removed. Before removing it, let Weir deliver the report it owes or give up on it.")]
    [InlineData(3, "Weir still owes Deluno on nas 3 reports on finished hand-offs. Nothing was removed. Before removing it, let Weir deliver the reports it owes or give up on them.")]
    public void Owed_reports_are_counted(int count, string expected) =>
        Assert.Equal(expected, new ConnectionRemovalBlockers([], 0, Reports(count)).RemovalRefusal(Label));

    [Fact]
    public void Everything_in_the_way_is_said_once_with_the_manager_named_the_first_time_only()
    {
        var blockers = new ConnectionRemovalBlockers([Workflow(1, "Movies")], 2, Reports(1));

        Assert.Equal(
            "Movies still comes from Deluno on nas. Weir is still working on 2 hand-offs from it. Weir still owes it a report on a finished hand-off. " +
            "Nothing was removed. Before removing it, unlink or remove that workflow, let the hand-offs finish or cancel them and let Weir deliver the report it owes or give up on it.",
            blockers.RemovalRefusal(Label));
    }

    [Theory]
    [InlineData(1, "Weir is still working on 1 hand-off from Deluno on nas, so its workflows stay linked. Nothing was changed. Let the hand-off finish or cancel it first.")]
    [InlineData(2, "Weir is still working on 2 hand-offs from Deluno on nas, so its workflows stay linked. Nothing was changed. Let the hand-offs finish or cancel them first.")]
    public void Unlinking_is_refused_while_hand_offs_are_in_progress(int count, string expected) =>
        Assert.Equal(expected, new ConnectionRemovalBlockers([Workflow(1, "Movies")], count, []).UnlinkRefusal(Label));

    [Fact]
    public void The_answer_carries_the_workflows_and_both_counts()
    {
        var wire = new ConnectionRemovalBlockers([Workflow(1, "Movies"), Workflow(2, "TV Shows")], 3, Reports(2)).ToWire();

        Assert.Equal(
            """{"workflows":[{"id":1,"name":"Movies"},{"id":2,"name":"TV Shows"}],"handoffs_in_progress":3,"reports":[{"handoff_id":"h1","name":"film1.mkv"},{"handoff_id":"h2","name":"film2.mkv"}]}""",
            WireJsonWriter.Dumps(wire, WireJsonFormat.Response));
    }
}

public sealed class GivenUpReportsTests
{
    [Fact]
    public void One_report_is_named_in_the_title_and_the_message_names_the_file()
    {
        var reports = new[] { new OwedReport("h1", "film.mkv") };

        Assert.Equal("Weir gave up on a report it owed Deluno on nas", GivenUpReports.Title("Deluno on nas", 1));
        Assert.Equal(
            """{"trigger":"manual","result":"success","reports":[{"handoff_id":"h1","name":"film.mkv"}],"message":"Weir will not tell Deluno on nas about this finished hand-off: film.mkv."}""",
            GivenUpReports.Detail("Deluno on nas", reports));
    }

    [Fact]
    public void Several_reports_are_counted_and_every_file_is_named()
    {
        var reports = new[] { new OwedReport("h1", "a.mkv"), new OwedReport("h2", "b.mkv") };

        Assert.Equal("Weir gave up on 2 reports it owed Deluno on nas", GivenUpReports.Title("Deluno on nas", 2));
        Assert.Contains("about these finished hand-offs: a.mkv, b.mkv.", GivenUpReports.Detail("Deluno on nas", reports), StringComparison.Ordinal);
    }
}
