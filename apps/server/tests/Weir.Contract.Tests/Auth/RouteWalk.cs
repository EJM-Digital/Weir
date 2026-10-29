using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Weir.Contract.Tests.Harness;
using Weir.Tests.Shared;

namespace Weir.Contract.Tests.Auth;

/// <summary>
/// How the route walk calls a route and reads its answer. A route is called with an id that names nothing, because a guard answers
/// before the server looks anything up, and with a body that holds everything the route requires except the token, so a refusal for a
/// missing token is the token's and not the body's.
/// </summary>
internal static partial class RouteWalk
{
    public const string TokenField = "csrf_token";
    public const string TokenHeader = "X-CSRF-Token";
    /// <summary>A token that is well formed and belongs to nobody, so a route that gets past its guard stops at the token and does nothing.</summary>
    public const string StrangerToken = "a-token-nobody-issued";
    public const string NotSignedIn = "Not authenticated.";
    public const string WrongRole = "Forbidden.";
    public const string ForeignOriginDetail = "Origin not allowed.";

    private static readonly Dictionary<string, string> PathValues = new(StringComparer.Ordinal)
    {
        ["source_key"] = "native",
        ["handoff_id"] = "handoff-1",
        ["session_id"] = "00000000000000000000000000000001",
        ["backup_id"] = "weir-backup.json",
        ["lane"] = "missing",
    };

    /// <summary>The fields a route requires besides the token. A route not listed requires nothing else.</summary>
    private static readonly Dictionary<string, Func<JsonObject>> RequiredFields = new(StringComparer.Ordinal)
    {
        ["POST /api/v1/activity/file-history/remove"] = () => new() { ["relative_path"] = "Films/Film (2020)/Film (2020).mkv" },
        ["POST /api/v1/auth/bootstrap"] = () => new() { ["username"] = "nobody", ["password"] = "a-long-enough-password-1" },
        ["POST /api/v1/auth/change-password"] = () => new() { ["current_password"] = "not-the-password", ["new_password"] = "another-long-password-2" },
        ["POST /api/v1/auth/change-username"] = () => new() { ["current_password"] = "not-the-password", ["new_username"] = "somebody" },
        ["POST /api/v1/auth/login"] = () => new() { ["username"] = "nobody", ["password"] = "not-the-password" },
        ["POST /api/v1/auth/theme"] = () => new() { ["theme"] = "dark" },
        ["POST /api/v1/download-clients/connections"] = () => new() { ["kind"] = "sabnzbd" },
        ["POST /api/v1/intake/handoffs/{source_key}/{handoff_id}/outcome"] = () => new() { ["outcome"] = "imported", ["occurredUtc"] = "2026-01-01T00:00:00Z" },
        ["POST /api/v1/media-managers/connections"] = () => new() { ["kind"] = "sonarr" },
        ["POST /api/v1/processing/files/{file_id}/manual-plan"] = () => new() { ["keep"] = new JsonArray(new JsonObject { ["index"] = 0 }), ["order"] = new JsonArray(0) },
        ["POST /api/v1/processing/jobs/file-remux-pass/enqueue"] = () => new() { ["relative_media_path"] = "Films/Film (2020)/Film (2020).mkv" },
        ["POST /api/v1/processing/libraries"] = () => new() { ["name"] = "Films", ["media_type"] = "movie" },
        ["POST /api/v1/processing/libraries/discover/{connection_id}/import"] = () => new() { ["keys"] = new JsonArray("1") },
        ["POST /api/v1/processing/libraries/reorder"] = () => new() { ["library_ids_in_order"] = new JsonArray(1) },
        ["POST /api/v1/processing/libraries/{library_id}/library-files/clean"] = () => new() { ["paths"] = new JsonArray("Films/Film (2020)/Film (2020).mkv") },
        ["POST /api/v1/processing/libraries/{library_id}/library-files/leave-alone"] = () => new() { ["path"] = "Films/Film (2020)/Film (2020).mkv" },
        ["POST /api/v1/processing/libraries/{library_id}/library-redownloads"] = () => new() { ["path"] = "Films/Film (2020)/Film (2020).mkv", ["confirm_destructive"] = false },
        ["POST /api/v1/processing/libraries/{library_id}/library-schedule"] = () => new() { ["enabled"] = false },
        ["POST /api/v1/processing/maintenance/run"] = () => new() { ["family"] = "work_temp_stale_sweep" },
        ["POST /api/v1/processing/rule-sets"] = () => new() { ["name"] = "Rules" },
        ["POST /api/v1/suite/notification-channels"] = () => new() { ["label"] = "Alerts", ["provider"] = "webhook", ["url"] = "http://localhost:9/alerts" },
        ["POST /api/v1/suite/operational-history/reset"] = () => new() { ["confirm"] = "not the word" },
        ["POST /api/v1/system/reconciliation/repair"] = () => new() { ["action"] = "no-such-repair" },
        ["PUT /api/v1/pause"] = () => new() { ["paused"] = false },
        ["PUT /api/v1/processing/libraries/{library_id}"] = () => new() { ["name"] = "Films", ["media_type"] = "movie" },
        ["PUT /api/v1/processing/operator-settings"] = () => new() { ["max_concurrent_files"] = 1 },
        ["PUT /api/v1/processing/rule-sets/{rule_set_id}"] = () => new() { ["name"] = "Rules" },
        ["PUT /api/v1/suite/configuration-bundle"] = () => new() { ["bundle"] = new JsonObject() },
        ["PUT /api/v1/suite/backup-password"] = () => new() { ["password"] = "route walk password", ["confirm_password"] = "route walk password" },
        ["PUT /api/v1/suite/home-network-sign-in"] = () => new() { ["enabled"] = false, ["current_password"] = "route walk password" },
        ["PUT /api/v1/suite/network-access"] = () => new() { ["scope"] = "this_pc_only" },
        ["PUT /api/v1/suite/notification-channels/{channel_id}"] = () => new() { ["label"] = "Alerts", ["provider"] = "webhook", ["url"] = "http://localhost:9/alerts" },
        ["PUT /api/v1/suite/settings"] = () => new() { ["app_timezone"] = "UTC", ["log_retention_days"] = 30 },
        ["PUT /api/v1/suite/update-settings"] = () => new() { ["mode"] = "NotifyOnly" },
    };

    /// <summary>The path with each <c>{name}</c> filled in: a number that names nothing, or the fixed value <see cref="PathValues"/> gives.</summary>
    public static string Url(RouteRule rule) =>
        PlaceholderPattern().Replace(rule.Path, match => PathValues.GetValueOrDefault(match.Groups[1].Value, "999999"));

    /// <summary>
    /// The body of a write: what the route requires besides the token, and the token when one is given. A body that is
    /// <paramref name="complete"/> false is an empty object, which every route refuses as incomplete.
    /// </summary>
    public static JsonObject Body(RouteRule rule, string? token, bool complete = true)
    {
        var body = complete && RequiredFields.TryGetValue(rule.Key, out var fields) ? fields() : [];
        if (complete && token is not null)
        {
            body[TokenField] = token;
        }

        return body;
    }

    /// <summary>
    /// Calls the route as <paramref name="client"/>, with the token in the body and in the header when one is given. A call that is not
    /// <paramref name="complete"/> sends an empty body: the guard has to answer before the server looks at what was asked.
    /// </summary>
    public static Task<WeirResponse> SendAsync(WeirClient client, RouteRule rule, string? token = null, string? origin = null, bool complete = true)
    {
        var headers = new Dictionary<string, string>();
        if (token is not null)
        {
            headers[TokenHeader] = token;
        }

        if (origin is not null)
        {
            headers["Origin"] = origin;
        }

        return rule.IsWrite
            ? client.RequestAsync(new HttpMethod(rule.Method), Url(rule), Body(rule, token, complete), headers)
            : client.GetAsync(Url(rule), headers);
    }

    /// <summary>The <c>detail</c> of an error answer, or null when the body is not an error object.</summary>
    public static string? Detail(WeirResponse response)
    {
        try
        {
            return response.Fields["detail"] is JsonValue detail && detail.TryGetValue<string>(out var text) ? text : null;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>True when the server turned the caller away at the door: not signed in, or an account that may not call this.</summary>
    public static bool TurnedAwayByAGuard(HttpStatusCode status, string? detail) =>
        status == HttpStatusCode.Unauthorized || (status == HttpStatusCode.Forbidden && detail is WrongRole or "Invalid account role.");

    /// <summary>
    /// True when the answer to a write that carried no token is the token's refusal: a 400 that names the token, or a 422 whose every
    /// complaint is about the token field (so the rest of the body was accepted).
    /// </summary>
    public static bool RefusesAMissingToken(WeirResponse response)
    {
        if (response.Status == HttpStatusCode.BadRequest)
        {
            return Detail(response) is { } detail && detail.Contains("token", StringComparison.OrdinalIgnoreCase);
        }

        if (response.Status != HttpStatusCode.UnprocessableEntity || response.Fields["detail"] is not JsonArray issues || issues.Count == 0)
        {
            return false;
        }

        return issues.All(issue => issue!["loc"]!.AsArray().Last()!.GetValue<string>() == TokenField);
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex PlaceholderPattern();
}
