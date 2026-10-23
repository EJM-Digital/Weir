using System.Text.RegularExpressions;
using Weir.Core.Json;

namespace Weir.Core.Observability;

/// <summary>Shared diagnostics vocabulary: secret redaction and result severities.</summary>
public static partial class Diagnostics
{
    public static readonly IReadOnlyList<string> SecretFieldFragments = ["api_key", "apikey", "authorization", "cookie", "password", "secret", "token"];

    /// <summary>Redacts a text value: all of it when the key looks secret, otherwise any <c>key=value</c> secret assignment inside it.</summary>
    public static string SanitizeText(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (SecretFieldFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            return "[redacted]";
        }

        return SecretAssignment().Replace(value, match => match.Groups[1].Value + "[redacted]");
    }

    /// <summary>The severity for a result: failed is an error, warning or retrying a warning, anything else info.</summary>
    public static string SeverityForResult(string result) => result.ToLowerInvariant() switch
    {
        "failed" => "error",
        "warning" or "retrying" => "warning",
        _ => "info",
    };

    [GeneratedRegex(@"(\b(?:api[_-]?key|authorization|cookie|password|secret|token)\b\s*[:=]\s*)[^,\s;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignment();
}

/// <summary>One diagnostics event in the shared vocabulary.</summary>
public sealed record DiagnosticEvent(
    string Module,
    string Action,
    string Trigger,
    string Result,
    string Severity,
    string? Provider = null,
    string? MediaScope = null,
    string? CorrelationId = null,
    string? Reason = null,
    string? NextAction = null,
    IReadOnlyList<KeyValuePair<string, long>>? Counts = null)
{
    /// <summary>The event as a payload: optional fields only when set, secret-looking values redacted.</summary>
    public WireObject AsSafeDict()
    {
        var payload = new WireObject()
            .Set("module", Module)
            .Set("action", Action)
            .Set("trigger", Trigger)
            .Set("result", Result)
            .Set("severity", Severity);
        foreach (var (key, value) in new[]
        {
            ("provider", Provider), ("media_scope", MediaScope), ("correlation_id", CorrelationId),
            ("reason", Reason), ("next_action", NextAction),
        })
        {
            if (!string.IsNullOrEmpty(value))
            {
                payload.Set(key, Diagnostics.SanitizeText(key, value));
            }
        }

        if (Counts is { Count: > 0 })
        {
            var counts = new WireObject();
            foreach (var (key, value) in Counts)
            {
                counts.Set(key, value);
            }

            payload.Set("counts", counts);
        }

        return payload;
    }
}
