using System.Net;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>Whether a workflow can be offered the reject policy, for each kind of manager and each way the manager can answer.</summary>
public sealed class RejectSupportEvaluatorTests : IDisposable
{
    private const string Manifest = "/api/integrations/external/manifest";

    private readonly MediaManagerFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private RejectSupportEvaluator Evaluator() => new(_fixture.Ports);

    private static ManagerConnection Connection(string kind, string name) => new(kind, name, "http://manager.local", "key");

    [Fact]
    public void The_evaluator_cannot_be_built_without_its_ports()
    {
        Assert.Throws<ArgumentNullException>(() => new RejectSupportEvaluator(null!));
    }

    [Fact]
    public async Task A_missing_list_of_connections_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Evaluator().EvaluateAsync(null!));
    }

    [Fact]
    public async Task A_workflow_with_no_manager_is_told_to_link_one()
    {
        var result = await Evaluator().EvaluateAsync([]);

        Assert.False(result.Available);
        Assert.Contains("Link a media manager", result.Reason);
    }

    [Theory]
    [InlineData("sonarr")]
    [InlineData("radarr")]
    public async Task A_manager_that_removes_queue_items_can_always_be_asked(string kind)
    {
        var result = await Evaluator().EvaluateAsync([Connection(kind, "Living room")]);

        Assert.True(result.Available);
        Assert.Contains($"{Connection(kind, "Living room").Label} can remove the download", result.Reason);
        Assert.Empty(_fixture.Http.Requests);
    }

    [Fact]
    public async Task A_manager_that_advertises_the_reject_capability_can_be_asked()
    {
        _fixture.Http.Json(HttpMethod.Get, Manifest, """{"capabilities":["processor-reject-regrab"],"libraries":[]}""");

        var result = await Evaluator().EvaluateAsync([Connection("deluno", "Films")]);

        Assert.True(result.Available);
        Assert.Contains("Deluno (Films) says it can blocklist a bad release", result.Reason);
    }

    [Fact]
    public async Task A_manager_that_does_not_advertise_the_reject_capability_is_not_asked()
    {
        _fixture.Http.Json(HttpMethod.Get, Manifest, """{"capabilities":[],"libraries":[]}""");

        var result = await Evaluator().EvaluateAsync([Connection("deluno", "Films")]);

        Assert.False(result.Available);
        Assert.Contains("Deluno (Films) does not yet say it can replace a rejected release", result.Reason);
    }

    [Fact]
    public async Task A_manager_that_cannot_be_asked_gives_its_own_reason()
    {
        _fixture.Http.Json(HttpMethod.Get, Manifest, """{"error":"nope"}""", HttpStatusCode.InternalServerError);

        var result = await Evaluator().EvaluateAsync([Connection("deluno", "Films")]);

        Assert.False(result.Available);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public async Task A_kind_weir_does_not_know_leaves_nothing_that_can_take_a_rejection()
    {
        var result = await Evaluator().EvaluateAsync([Connection("unheard-of", "Mystery")]);

        Assert.False(result.Available);
        Assert.Equal("None of the linked media managers can take a rejection.", result.Reason);
    }

    [Fact]
    public async Task The_first_manager_that_can_take_a_rejection_wins_over_those_before_it()
    {
        _fixture.Http.Json(HttpMethod.Get, Manifest, """{"capabilities":[],"libraries":[]}""");

        var result = await Evaluator().EvaluateAsync([Connection("deluno", "Films"), Connection("sonarr", "Shows")]);

        Assert.True(result.Available);
        Assert.Contains("Sonarr (Shows) can remove the download", result.Reason);
    }

    [Fact]
    public async Task Every_manager_that_cannot_is_named_in_the_reason()
    {
        _fixture.Http.Json(HttpMethod.Get, Manifest, """{"capabilities":[],"libraries":[]}""");

        var result = await Evaluator().EvaluateAsync([Connection("deluno", "Films"), Connection("deluno", "Shows")]);

        Assert.False(result.Available);
        Assert.Contains("Deluno (Films) does not yet say", result.Reason);
        Assert.Contains("Deluno (Shows) does not yet say", result.Reason);
    }
}
