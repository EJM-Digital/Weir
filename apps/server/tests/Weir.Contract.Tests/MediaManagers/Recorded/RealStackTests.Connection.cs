using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

public sealed partial class RealStackTests
{
    private const string ManifestPath = "/api/integrations/external/manifest";

    [Fact]
    public async Task The_manifest_the_real_Deluno_answers_is_read_into_what_it_manages_and_where_its_libraries_are()
    {
        var connection = RealStackSide.Load("real-connection");
        var manifest = RealStackSide.Only(connection, exchange => exchange.Request.Path == ManifestPath);
        await using var world = await RealStackSide.StartWorldAsync(RealStackSide.Load("real-release-folder"));
        world.Deluno.Route("GET", ManifestPath, _ => DelunoSide.AnswerOf(manifest.Reply));
        var recorded = manifest.Reply.Body!["libraries"]!.AsArray();

        var row = Assert.Single((await world.Scenario.Admin.GetAsync($"{WeirClient.Api}/media-managers/capabilities")).Elements)!.AsObject();

        Assert.True((bool)row["reachable"]!);
        Assert.Equal(["movie"], row["media_scopes"]!.AsArray().Select(scope => (string)scope!));
        Assert.Equal(recorded.Select(library => (string)library!["rootPath"]!), row["library_roots"]!.AsArray().Select(root => (string)root!));
        Assert.True((bool)row["reports_import_queue"]!);
        Assert.Equal(world.Deluno.ApiKey, Assert.Single(world.Deluno.RequestsTo("GET", ManifestPath)).Header("X-Api-Key"));
    }
}
