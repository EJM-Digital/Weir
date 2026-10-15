using System.Net;
using System.Text;
using Weir.Contract.Tests.Harness;
using Weir.Tests.Shared;

namespace Weir.Contract.Tests.Auth;

/// <summary>
/// Every write route checks, in this order: who is asking, how big the body is, what type it says it is, and then the body itself.
/// An anonymous caller learns nothing about a route's body rules, and a caller who may not use the route is told so before anything
/// about the body. The walk takes the routes from <c>docs/coverage/route-access.json</c>, so a route added later is held to the order
/// without an edit here.
/// </summary>
[ContractArea("auth")]
public sealed class RouteCheckOrderTests(RouteAccessFixture fixture) : IClassFixture<RouteAccessFixture>
{
    private const string Json = "application/json";

    private static readonly Dictionary<string, RouteRule> RulesByKey = RouteAccessTable.Rules.ToDictionary(rule => rule.Key, StringComparer.Ordinal);

    /// <summary>One byte over the 8 MB the server reads, and then some.</summary>
    private static readonly byte[] NineMegabytes = Encoding.UTF8.GetBytes("{\"padding\":\"" + new string('a', 9 * 1024 * 1024) + "\"}");

    /// <summary>
    /// The writes that carry their token in <c>X-CSRF-Token</c> and read nothing from the body. Only the size of a body is held to
    /// the order for them: whatever else is sent is not looked at, so there is no type or content to refuse.
    /// </summary>
    private static readonly HashSet<string> IgnoreTheirBody = new(StringComparer.Ordinal)
    {
        "POST /api/v1/auth/sessions/revoke-others",
        "POST /api/v1/auth/sessions/{session_id}/revoke",
        "DELETE /api/v1/suite/notification-channels/{channel_id}",
    };

    /// <summary>The writes that are not guarded by a sign-in, each with the reason it keeps its own order.</summary>
    private static readonly Dictionary<string, string> KeepTheirOwnOrder = new(StringComparer.Ordinal)
    {
        ["POST /api/v1/auth/login"] = "Signing in is anonymous by design; the sign-in limits and the token come with the request.",
        ["POST /api/v1/auth/bootstrap"] = "Creating the first account is anonymous by design, and refused once an account exists.",
        ["POST /api/v1/auth/logout"] = "Ending a session needs no sign-in.",
        ["POST /api/v1/intake/webhook/{source_key}"] = "A media manager proves itself with its webhook secret, which is checked with the body.",
        ["POST /api/v1/intake/handoffs/{source_key}/{handoff_id}/outcome"] = "A media manager proves itself with its webhook secret, which is checked with the body.",
        ["DELETE /api/v1/intake/handoffs/{source_key}/{handoff_id}"] = "A media manager proves itself with its webhook secret; the route reads no body.",
    };

    private static readonly (string Name, byte[] Bytes, string ContentType, HttpStatusCode Signed)[] Bodies =
    [
        ("a 9 MB body", NineMegabytes, Json, HttpStatusCode.RequestEntityTooLarge),
        ("a body that is not JSON", Encoding.UTF8.GetBytes("not json at all"), "text/plain", HttpStatusCode.UnsupportedMediaType),
        ("invalid JSON", Encoding.UTF8.GetBytes("{\"csrf_token\": "), Json, HttpStatusCode.UnprocessableEntity),
        ("a JSON body with the wrong fields", Encoding.UTF8.GetBytes("{\"unexpected\":1}"), Json, HttpStatusCode.UnprocessableEntity),
    ];

    public static TheoryData<string> Guarded => Keys(rule => rule.IsWrite && rule.Access is RouteAccess.SignedIn or RouteAccess.Operator or RouteAccess.Admin);

    public static TheoryData<string> BeyondAViewer => Keys(rule => rule.IsWrite && rule.Access is RouteAccess.Operator or RouteAccess.Admin);

    public static TheoryData<string> AdminOnly => Keys(rule => rule.IsWrite && rule.Access == RouteAccess.Admin);

    public static TheoryData<string> ReadAnonymously => Keys(rule => KeepTheirOwnOrder.ContainsKey(rule.Key) && rule.Method == "POST");

    [Fact]
    public void Every_write_is_either_checked_in_order_or_says_why_it_keeps_its_own()
    {
        var unguarded = RouteAccessTable.Rules.Where(rule => rule.IsWrite && rule.Access is RouteAccess.Public or RouteAccess.ManagerKey).Select(rule => rule.Key);

        Assert.Equal(KeepTheirOwnOrder.Keys.Order(StringComparer.Ordinal), unguarded.Order(StringComparer.Ordinal));
        Assert.All(IgnoreTheirBody, key => Assert.True(RulesByKey.ContainsKey(key), key + " is not in the access table."));
    }

    [Theory]
    [MemberData(nameof(Guarded))]
    public async Task An_anonymous_caller_is_refused_before_the_body_is_looked_at(string key)
    {
        var rule = RulesByKey[key];

        foreach (var body in Bodies)
        {
            var response = await SendAsync(fixture.Anonymous, rule, body);

            Assert.True(
                response.Status == HttpStatusCode.Unauthorized && RouteWalk.Detail(response) == RouteWalk.NotSignedIn,
                $"{key} with {body.Name}: {response}");
        }
    }

    [Theory]
    [MemberData(nameof(BeyondAViewer))]
    public async Task A_caller_who_may_not_use_the_route_is_refused_before_the_body_is_looked_at(string key)
    {
        var rule = RulesByKey[key];
        var callers = rule.Access == RouteAccess.Admin
            ? new[] { ("a viewer", fixture.Viewer), ("an operator", fixture.Operator) }
            : [("a viewer", fixture.Viewer)];

        foreach (var (who, client) in callers)
        {
            foreach (var body in Bodies)
            {
                var response = await SendAsync(client, rule, body);

                Assert.True(
                    response.Status == HttpStatusCode.Forbidden && RouteWalk.Detail(response) == RouteWalk.WrongRole,
                    $"{key} for {who} with {body.Name}: {response}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Guarded))]
    public async Task A_caller_who_may_use_the_route_gets_the_size_then_the_type_then_the_body_refused(string key)
    {
        var rule = RulesByKey[key];
        var client = LowestAllowed(rule);

        foreach (var body in IgnoreTheirBody.Contains(key) ? Bodies.Take(1) : Bodies)
        {
            var response = await SendAsync(client, rule, body);

            Assert.True(response.Status == body.Signed, $"{key} with {body.Name}, expected {(int)body.Signed}: {response}");
        }
    }

    [Theory]
    [MemberData(nameof(ReadAnonymously))]
    public async Task A_route_open_to_all_still_refuses_a_body_over_the_size_limit(string key)
    {
        var rule = RulesByKey[key];

        var response = await SendAsync(fixture.Anonymous, rule, Bodies[0]);

        Assert.True(response.Status == HttpStatusCode.RequestEntityTooLarge, $"{key}: {response}");
    }

    private WeirClient LowestAllowed(RouteRule rule) => rule.Access switch
    {
        RouteAccess.SignedIn => fixture.Viewer,
        RouteAccess.Operator => fixture.Operator,
        _ => fixture.Admin,
    };

    private static Task<WeirResponse> SendAsync(WeirClient client, RouteRule rule, (string Name, byte[] Bytes, string ContentType, HttpStatusCode Signed) body) =>
        client.RequestRawAsync(new HttpMethod(rule.Method), RouteWalk.Url(rule), body.Bytes, body.ContentType);

    private static TheoryData<string> Keys(Func<RouteRule, bool> where)
    {
        var keys = new TheoryData<string>();
        foreach (var rule in RouteAccessTable.Rules.Where(where))
        {
            keys.Add(rule.Key);
        }

        return keys;
    }
}
