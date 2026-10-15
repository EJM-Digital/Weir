using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using static Weir.Contract.Tests.Libraries.LibrariesPartAHelpers;
using static Weir.Contract.Tests.Libraries.LibraryRequests;

namespace Weir.Contract.Tests.Libraries;

/// <summary>
/// A profile (a rule set) that a workflow uses cannot be removed, and the refusal says which workflows still need it. One nobody uses can
/// be removed, and only that one goes. However many removals are tried, every workflow still has a profile that exists.
/// </summary>
[ContractArea("libraries")]
public sealed class RemovingAProfileTests
{
    private static async Task<JsonObject> CreateProfileAsync(WeirClient admin, string name)
    {
        var created = await admin.PostWithCsrfAsync(RuleSets, Obj(("name", name), ("primary_audio_lang", "eng")));
        created.ShouldBe(HttpStatusCode.Created);
        return created.Fields;
    }

    private static async Task<JsonObject> CreateWorkflowAsync(WeirClient admin, string name, JsonNode? profile)
    {
        var created = await admin.PostWithCsrfAsync(
            LibrariesUrl,
            Obj(("enabled", false), ("name", name), ("media_type", "movie"), ("watched_folder", $"/srv/{name}/in"), ("output_folder", $"/srv/{name}/out"), ("rule_set_id", profile)));
        created.ShouldBe(HttpStatusCode.Created);
        return created.Fields;
    }

    private static async Task<List<long>> ProfileIdsAsync(WeirClient admin) =>
        [.. (await admin.GetAsync(RuleSets)).Elements.Select(row => (long)row!["id"]!)];

    private static async Task AssertEveryWorkflowHasAProfileAsync(WeirClient admin)
    {
        var profiles = await ProfileIdsAsync(admin);
        var workflows = (await admin.GetAsync(LibrariesUrl)).Elements;
        Assert.NotEmpty(workflows);
        Assert.All(workflows, workflow => Assert.Contains((long)(workflow!["rule_set_id"] ?? throw new InvalidOperationException($"{workflow["name"]} has no profile")), profiles));
    }

    [Fact]
    public async Task A_profile_a_workflow_uses_is_refused_with_the_reason_and_stays_with_that_workflow()
    {
        await using var server = await WeirServer.StartNewAsync();
        using var admin = await server.CreateAdminClientAsync();
        var profile = await CreateProfileAsync(admin, "Anime");
        var workflow = await CreateWorkflowAsync(admin, "Anime Films", profile["id"]!.DeepClone());

        var refused = await admin.DeleteWithCsrfBodyAsync($"{RuleSets}/{(long)profile["id"]!}");

        refused.ShouldBe(HttpStatusCode.Conflict);
        Assert.Equal(
            "Anime is still used by 1 workflow. Point them at another rule set first — removing it would strip their audio and subtitle handling.",
            (string)refused.Fields["detail"]!);
        Assert.Contains((long)profile["id"]!, await ProfileIdsAsync(admin));
        var again = await admin.GetAsync($"{LibrariesUrl}/{(long)workflow["id"]!}");
        Assert.Equal((long)profile["id"]!, (long)again.Fields["rule_set_id"]!);
        await AssertEveryWorkflowHasAProfileAsync(admin);
    }

    [Fact]
    public async Task A_profile_nobody_uses_is_removed_and_only_that_one_goes()
    {
        await using var server = await WeirServer.StartNewAsync();
        using var admin = await server.CreateAdminClientAsync();
        var before = await ProfileIdsAsync(admin);
        var unused = await CreateProfileAsync(admin, "Unused");
        var neighbour = await CreateProfileAsync(admin, "Neighbour");

        var removed = await admin.DeleteWithCsrfBodyAsync($"{RuleSets}/{(long)unused["id"]!}");

        removed.ShouldBe(HttpStatusCode.NoContent);
        Assert.Equal([.. before, (long)neighbour["id"]!], await ProfileIdsAsync(admin));
        (await admin.DeleteWithCsrfBodyAsync($"{RuleSets}/{(long)unused["id"]!}")).ShouldBe(HttpStatusCode.NotFound);
        await AssertEveryWorkflowHasAProfileAsync(admin);
    }

    [Fact]
    public async Task A_profile_becomes_removable_once_the_workflow_that_used_it_has_another()
    {
        await using var server = await WeirServer.StartNewAsync();
        using var admin = await server.CreateAdminClientAsync();
        var first = await CreateProfileAsync(admin, "First");
        var second = await CreateProfileAsync(admin, "Second");
        var workflow = await CreateWorkflowAsync(admin, "Switching", first["id"]!.DeepClone());
        (await admin.DeleteWithCsrfBodyAsync($"{RuleSets}/{(long)first["id"]!}")).ShouldBe(HttpStatusCode.Conflict);

        var body = LibraryBodies.Unchanged(workflow);
        body["rule_set_id"] = second["id"]!.DeepClone();
        var switched = await admin.PutWithCsrfAsync($"{LibrariesUrl}/{(long)workflow["id"]!}", body);
        switched.ShouldBe(HttpStatusCode.OK);
        var removed = await admin.DeleteWithCsrfBodyAsync($"{RuleSets}/{(long)first["id"]!}");

        removed.ShouldBe(HttpStatusCode.NoContent);
        await AssertEveryWorkflowHasAProfileAsync(admin);
    }

    [Fact]
    public async Task The_profile_a_workflow_was_given_when_it_chose_none_is_in_use_and_cannot_be_removed()
    {
        await using var server = await WeirServer.StartNewAsync();
        using var admin = await server.CreateAdminClientAsync();
        var workflow = await CreateWorkflowAsync(admin, "Chose None", profile: null);
        var given = (long)workflow["rule_set_id"]!;

        var refused = await admin.DeleteWithCsrfBodyAsync($"{RuleSets}/{given}");

        refused.ShouldBe(HttpStatusCode.Conflict);
        Assert.Contains("is still used by", (string)refused.Fields["detail"]!, StringComparison.Ordinal);
        Assert.Contains(given, await ProfileIdsAsync(admin));
        await AssertEveryWorkflowHasAProfileAsync(admin);
    }
}
