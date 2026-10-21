using System.Net;
using System.Runtime.CompilerServices;
using Weir.Api.Tests.Platform;

namespace Weir.Api.Tests;

/// <summary>
/// The four kinds of caller a route can see: nobody signed in, a viewer, an operator and an administrator. A test says the lowest
/// role a route lets in and <see cref="AssertWhoMayCallAsync"/> checks that every caller below it is turned away with the right
/// status and every caller at or above it gets past the guard.
/// </summary>
internal static class Callers
{
    public const string Anonymous = "anonymous";
    public const string Viewer = "viewer";
    public const string Operator = "operator";
    public const string Admin = "admin";

    private static readonly string[] Ladder = [Anonymous, Viewer, Operator, Admin];
    private static readonly ConditionalWeakTable<WeirTestServer, Dictionary<string, ApiTestClient>> Clients = new();

    /// <summary>A client signed in as the given role, signed in once per server (sign-in is rate limited); the admin account is the one every test server seeds.</summary>
    public static async Task<ApiTestClient> SignedInAsync(WeirTestServer server, string role)
    {
        var known = Clients.GetOrCreateValue(server);
        if (known.TryGetValue(role, out var existing))
        {
            return existing;
        }

        var client = new ApiTestClient(server);
        switch (role)
        {
            case Anonymous:
                break;
            case Admin:
                if (await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM users WHERE username = 'alice'") == 0)
                {
                    await TestDatabase.SeedAdminAsync(server);
                }

                await client.SignInAsync();
                break;
            default:
                if (await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM users WHERE username = $u", ("$u", role)) == 0)
                {
                    await TestDatabase.SeedUserAsync(server, role, ApiTestClient.ViewerPassword, role);
                }

                await client.SignInAsync(role, ApiTestClient.ViewerPassword);
                break;
        }

        known[role] = client;
        return client;
    }

    /// <summary>
    /// Calls <paramref name="path"/> as each kind of caller with a body that lacks every field: a caller who may not use the route
    /// is answered 401 (nobody signed in) or 403 before the body is read, and one who may gets past the guard to the body's own
    /// refusal (422) or the read's answer.
    /// </summary>
    public static async Task AssertWhoMayCallAsync(WeirTestServer server, HttpMethod method, string path, string lowestRole)
    {
        var lowest = Array.IndexOf(Ladder, lowestRole);
        foreach (var role in Ladder)
        {
            var client = await SignedInAsync(server, role);
            using var response = await client.SendAsync(method, path, method == HttpMethod.Get ? null : new { });
            var allowed = Array.IndexOf(Ladder, role) >= lowest;
            var expected = allowed ? "past the guard" : role == Anonymous ? HttpStatusCode.Unauthorized.ToString() : HttpStatusCode.Forbidden.ToString();
            var actual = allowed
                ? response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? response.StatusCode.ToString() : "past the guard"
                : response.StatusCode.ToString();
            Assert.True(expected == actual, $"{role} {method} {path}: expected {expected}, got {actual}.");
        }
    }
}
