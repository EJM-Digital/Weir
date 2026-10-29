using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weir.Core.Auth;
using Weir.Infrastructure.Http;
using Weir.Infrastructure.Tests;

namespace Weir.Api.Tests.Platform;

/// <summary>What the recovery-code API tests share: seeded codes, the recovery requests, and a log and an alert sink to read afterwards.</summary>
internal static class RecoverySupport
{
    public const string Metrics = "/metrics";
    public const string MetricsToken = "metrics-bearer-token-for-recovery-tests";

    /// <summary>Ten codes stored the way the product stores them, hashed at the cheapest cost so the test does not spend a second on each.</summary>
    public static async Task<IReadOnlyList<string>> SeedCodesAsync(WeirTestServer server, long userId = 1)
    {
        var set = RecoveryCodeRules.NewSet();
        foreach (var code in set)
        {
            await TestDatabase.ExecuteAsync(
                server,
                "INSERT INTO recovery_codes (user_id, lookup, code_hash) VALUES ($user, $lookup, $hash)",
                ("$user", userId),
                ("$lookup", code[..5]),
                ("$hash", CheapPasswordHash.For(code)));
        }

        return set;
    }

    public static async Task<HttpResponseMessage> SpendAsync(ApiTestClient client, string code, string? fromAddress = null) =>
        await client.PostAsync(
            "/api/v1/auth/recovery/code",
            new { code, csrf_token = await client.CsrfAsync() },
            fromAddress is null ? null : new Dictionary<string, string> { ["X-Forwarded-For"] = fromAddress });

    public static async Task<HttpResponseMessage> ChoosePasswordAsync(ApiTestClient client, string token, string password) =>
        await client.PostAsync("/api/v1/auth/recovery/password", new { recovery_token = token, new_password = password, csrf_token = await client.CsrfAsync() });

    public static Task<WeirTestServer> StartAsync(
        CapturingLoggerFactory? logs = null, RecordingAlerts? alerts = null, params (string Name, string Value)[] variables) =>
        WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", ApiTestClient.Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0"), ("WEIR_METRICS_BEARER_TOKEN", MetricsToken), .. variables],
            configureServices: services =>
            {
                if (logs is not null)
                {
                    services.AddSingleton<ILoggerFactory>(logs);
                }

                if (alerts is not null)
                {
                    services.AddSingleton<IExternalJsonPoster>(alerts);
                }
            });

    /// <summary>Every message the server logged, all in one string.</summary>
    public static string Everything(CapturingLoggerFactory logs) => string.Join('\n', logs.Entries.Select(entry => entry.Message));

    public static bool LoggedAtOrAbove(CapturingLoggerFactory logs, LogLevel level, string text) =>
        logs.Entries.Any(entry => entry.Level >= level && entry.Message.Contains(text, StringComparison.Ordinal));
}

/// <summary>The alerts the server posts, kept with their bodies.</summary>
internal sealed class RecordingAlerts : IExternalJsonPoster
{
    private readonly ConcurrentQueue<(string Url, string Body)> _posts = new();

    public List<(string Url, string Body)> Posts => [.. _posts];

    public Task<int> PostJsonAsync(string url, byte[] body, IReadOnlyDictionary<string, string> headers, TimeSpan timeout, CancellationToken cancellationToken)
    {
        _posts.Enqueue((url, System.Text.Encoding.UTF8.GetString(body)));
        return Task.FromResult(204);
    }
}
