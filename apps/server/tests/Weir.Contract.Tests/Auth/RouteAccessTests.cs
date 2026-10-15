using System.Net;
using Weir.Contract.Tests.Harness;
using Weir.Tests.Shared;

namespace Weir.Contract.Tests.Auth;

/// <summary>
/// Walks every route in <c>docs/coverage/route-access.json</c> against a running server: who is let in, who is turned away, and what a
/// write needs besides a sign-in. The file is the one place that says who may call each route, so a route that is not in it fails
/// (<c>RouteAccessTableTests</c> in the API tests keeps the file whole from the server's side), and a route whose guard differs from its
/// line fails here, with the answer the server gave.
/// </summary>
[ContractArea("auth")]
public sealed class RouteAccessTests(RouteAccessFixture fixture) : IClassFixture<RouteAccessFixture>
{
    private static readonly Dictionary<string, RouteRule> RulesByKey = RouteAccessTable.Rules.ToDictionary(rule => rule.Key, StringComparer.Ordinal);

    private static readonly RouteAccess[] SignInRequired = [RouteAccess.SignedIn, RouteAccess.Operator, RouteAccess.Admin];

    public static TheoryData<string> Guarded => Keys(rule => SignInRequired.Contains(rule.Access));

    public static TheoryData<string> BeyondAViewer => Keys(rule => rule.Access is RouteAccess.Operator or RouteAccess.Admin);

    public static TheoryData<string> AdminOnly => Keys(rule => rule.Access == RouteAccess.Admin);

    public static TheoryData<string> Open => Keys(rule => rule.Access == RouteAccess.Public);

    public static TheoryData<string> ForMediaManagers => Keys(rule => rule.Access == RouteAccess.ManagerKey);

    /// <summary>A write a browser makes, so one the token and the origin guard.</summary>
    public static TheoryData<string> BrowserWrites => Keys(rule => rule.IsWrite && rule.Access != RouteAccess.ManagerKey);

    [Fact]
    public async Task Every_operation_the_server_describes_is_in_the_access_table()
    {
        var response = await fixture.Anonymous.GetAsync("/openapi.json");

        var described = response.Fields["paths"]!.AsObject()
            .SelectMany(path => path.Value!.AsObject().Select(operation => $"{operation.Key.ToUpperInvariant()} {path.Key}"))
            .ToList();
        var missing = described.Where(key => !RulesByKey.ContainsKey(key)).Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "Add these to docs/coverage/route-access.json, each with who may call it: " + string.Join(", ", missing));
    }

    [Theory]
    [MemberData(nameof(Guarded))]
    public async Task An_anonymous_caller_is_refused(string key)
    {
        var rule = RulesByKey[key];

        var response = await RouteWalk.SendAsync(fixture.Anonymous, rule, complete: false);

        Assert.True(response.Status == HttpStatusCode.Unauthorized && RouteWalk.Detail(response) == RouteWalk.NotSignedIn, $"{key}: {response}");
    }

    [Theory]
    [MemberData(nameof(BeyondAViewer))]
    public async Task A_viewer_is_refused_what_needs_an_operator(string key)
    {
        var rule = RulesByKey[key];

        var response = await RouteWalk.SendAsync(fixture.Viewer, rule, complete: false);

        Assert.True(response.Status == HttpStatusCode.Forbidden && RouteWalk.Detail(response) == RouteWalk.WrongRole, $"{key}: {response}");
    }

    [Theory]
    [MemberData(nameof(AdminOnly))]
    public async Task An_operator_is_refused_what_needs_an_administrator(string key)
    {
        var rule = RulesByKey[key];

        var response = await RouteWalk.SendAsync(fixture.Operator, rule, complete: false);

        Assert.True(response.Status == HttpStatusCode.Forbidden && RouteWalk.Detail(response) == RouteWalk.WrongRole, $"{key}: {response}");
    }

    [Theory]
    [MemberData(nameof(Guarded))]
    public async Task Every_role_the_route_names_is_let_past_the_guard(string key)
    {
        var rule = RulesByKey[key];

        foreach (var (role, client) in AllowedRoles(rule))
        {
            var (status, detail) = await AnswerAsync(client, rule);

            Assert.False(RouteWalk.TurnedAwayByAGuard(status, detail), $"{key} turned away {role}: HTTP {(int)status} {detail}");
        }
    }

    [Theory]
    [MemberData(nameof(Open))]
    public async Task A_public_route_answers_a_caller_who_is_not_signed_in(string key)
    {
        var rule = RulesByKey[key];

        var response = await RouteWalk.SendAsync(fixture.Anonymous, rule, RouteWalk.StrangerToken);

        Assert.False(RouteWalk.TurnedAwayByAGuard(response.Status, RouteWalk.Detail(response)), $"{key}: {response}");
    }

    [Theory]
    [MemberData(nameof(ForMediaManagers))]
    public async Task A_media_manager_route_refuses_a_caller_without_the_managers_key_whoever_is_signed_in(string key)
    {
        var rule = RulesByKey[key];

        foreach (var (who, client) in new[] { ("an anonymous caller", fixture.Anonymous), ("the administrator's session", fixture.Admin) })
        {
            var response = await RouteWalk.SendAsync(client, rule);

            Assert.True(response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"{key} for {who}: {response}");
        }
    }

    [Theory]
    [MemberData(nameof(BrowserWrites))]
    public async Task A_write_without_a_token_is_refused_for_the_token(string key)
    {
        var rule = RulesByKey[key];

        var response = await RouteWalk.SendAsync(WriterOf(rule), rule);

        Assert.True(RouteWalk.RefusesAMissingToken(response), $"{key}: {response}");
    }

    [Theory]
    [MemberData(nameof(BrowserWrites))]
    public async Task A_write_from_another_origin_is_refused_even_with_a_good_token(string key)
    {
        var rule = RulesByKey[key];
        var writer = WriterOf(rule);

        var response = await RouteWalk.SendAsync(writer, rule, await writer.CsrfTokenAsync(), RouteAccessFixture.ForeignOrigin);

        Assert.True(response.Status == HttpStatusCode.Forbidden && RouteWalk.Detail(response) == RouteWalk.ForeignOriginDetail, $"{key}: {response}");
    }

    private static TheoryData<string> Keys(Func<RouteRule, bool> where)
    {
        var keys = new TheoryData<string>();
        foreach (var rule in RouteAccessTable.Rules.Where(where))
        {
            keys.Add(rule.Key);
        }

        return keys;
    }

    /// <summary>The accounts the route admits, lowest first.</summary>
    private IEnumerable<(string Role, WeirClient Client)> AllowedRoles(RouteRule rule)
    {
        if (rule.Access == RouteAccess.SignedIn)
        {
            yield return ("a viewer", fixture.Viewer);
        }

        if (rule.Access is RouteAccess.SignedIn or RouteAccess.Operator)
        {
            yield return ("an operator", fixture.Operator);
        }

        yield return ("an administrator", fixture.Admin);
    }

    /// <summary>The caller whose writes the token and origin checks are tried on: nobody for a public route, the administrator for the rest.</summary>
    private WeirClient WriterOf(RouteRule rule) => rule.Access == RouteAccess.Public ? fixture.Anonymous : fixture.Admin;

    /// <summary>The status and detail of the answer. A stream is not read to its end, since it has none.</summary>
    private static async Task<(HttpStatusCode Status, string? Detail)> AnswerAsync(WeirClient client, RouteRule rule)
    {
        if (rule.Path.EndsWith("/stream", StringComparison.Ordinal))
        {
            using var stream = await client.OpenStreamAsync(RouteWalk.Url(rule));
            return (stream.Status, null);
        }

        var response = await RouteWalk.SendAsync(client, rule, RouteWalk.StrangerToken);
        return (response.Status, RouteWalk.Detail(response));
    }
}
