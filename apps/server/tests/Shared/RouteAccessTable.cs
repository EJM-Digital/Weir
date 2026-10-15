using System.Text.Json.Nodes;

namespace Weir.Tests.Shared;

/// <summary>Who may call a route, as <c>docs/coverage/route-access.json</c> declares it.</summary>
internal enum RouteAccess
{
    /// <summary>Anyone, signed in or not.</summary>
    Public,

    /// <summary>Any account: viewer, operator or administrator.</summary>
    SignedIn,

    /// <summary>An operator or an administrator.</summary>
    Operator,

    /// <summary>An administrator only.</summary>
    Admin,

    /// <summary>A media manager that presents its webhook secret; a browser session does not open it.</summary>
    ManagerKey,
}

/// <summary>One route of <c>route-access.json</c>: the method, the path as the server maps it, and who may call it.</summary>
internal sealed record RouteRule(string Method, string Path, RouteAccess Access, string? Reason)
{
    public string Key => $"{Method} {Path}";

    public bool IsWrite => Method != "GET";
}

/// <summary>
/// The declared access of every route, read from <c>route-access.json</c> beside the test assembly. The file is the one place that says who
/// may call what; the tests check the server against it, so a route that is not listed, or whose guard differs, fails.
/// </summary>
internal static class RouteAccessTable
{
    public const string FileName = "route-access.json";

    private static readonly Dictionary<string, RouteAccess> AccessNames = new(StringComparer.Ordinal)
    {
        ["public"] = RouteAccess.Public,
        ["signed-in"] = RouteAccess.SignedIn,
        ["operator"] = RouteAccess.Operator,
        ["admin"] = RouteAccess.Admin,
        ["manager-key"] = RouteAccess.ManagerKey,
    };

    private static readonly Lazy<List<RouteRule>> Loaded = new(Read);

    public static IReadOnlyList<RouteRule> Rules => Loaded.Value;

    private static List<RouteRule> Read()
    {
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, FileName)))!.AsObject();
        var rules = new List<RouteRule>();
        foreach (var entry in document["routes"]!.AsArray().Select(node => node!.AsObject()))
        {
            var key = $"{(string)entry["method"]!} {(string)entry["path"]!}";
            var access = AccessNames.TryGetValue((string)entry["access"]!, out var named)
                ? named
                : throw new InvalidDataException($"{FileName}: '{key}' names the access '{(string)entry["access"]!}', which is not one of {string.Join(", ", AccessNames.Keys)}.");
            var reason = (string?)entry["reason"];
            if (access is RouteAccess.Public or RouteAccess.ManagerKey && string.IsNullOrWhiteSpace(reason))
            {
                throw new InvalidDataException($"{FileName}: '{key}' is {(string)entry["access"]!} and has to say why in 'reason'.");
            }

            rules.Add(new RouteRule((string)entry["method"]!, (string)entry["path"]!, access, reason));
        }

        var repeated = rules.GroupBy(rule => rule.Key, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        return repeated.Count == 0
            ? rules
            : throw new InvalidDataException($"{FileName} lists these more than once: {string.Join(", ", repeated)}.");
    }
}
