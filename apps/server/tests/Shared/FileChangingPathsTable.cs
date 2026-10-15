using System.Text.Json.Nodes;

namespace Weir.Tests.Shared;

/// <summary>How the pause treats a path that can change a media file.</summary>
internal enum PauseGate
{
    /// <summary>A job kind the worker claim refuses while paused.</summary>
    Claim,

    /// <summary>The one job kind that may run while paused, because its scan changes nothing.</summary>
    Scan,

    /// <summary>A request a person makes by hand, refused while paused.</summary>
    Refused,

    /// <summary>Deliberately not held by the pause, for the reason it gives.</summary>
    Exempt,
}

/// <summary>One way Weir can change a media file: what starts it, how the pause holds it, and the tests that show it.</summary>
internal sealed record FileChangingPath(string Id, string What, PauseGate Gate, string? JobKind, IReadOnlyList<string> Routes, string? Because, IReadOnlyList<string> Proofs);

/// <summary>A server source file that changes files or calls a helper that does, and the paths it serves.</summary>
internal sealed record FileChangingSite(string File, int Calls, IReadOnlyList<string> Seams, IReadOnlyList<string> Paths);

/// <summary>
/// <c>docs/coverage/file-changing-paths.json</c>, read from the repository: every way Weir can change a media file and what holds it back
/// while processing is paused. The tests compare it with the server's own source, job kinds and routes.
/// </summary>
internal static class FileChangingPathsTable
{
    public const string RelativePath = "docs/coverage/file-changing-paths.json";

    private static readonly Dictionary<string, PauseGate> GateNames = new(StringComparer.Ordinal)
    {
        ["claim"] = PauseGate.Claim,
        ["scan"] = PauseGate.Scan,
        ["refused"] = PauseGate.Refused,
        ["exempt"] = PauseGate.Exempt,
    };

    private static readonly Lazy<(string Root, List<FileChangingPath> Paths, List<FileChangingSite> Sites)> Loaded = new(Read);

    public static string RepositoryRoot => Loaded.Value.Root;

    public static IReadOnlyList<FileChangingPath> Paths => Loaded.Value.Paths;

    public static IReadOnlyList<FileChangingSite> Sites => Loaded.Value.Sites;

    private static (string, List<FileChangingPath>, List<FileChangingSite>) Read()
    {
        var root = FindRoot();
        var document = JsonNode.Parse(File.ReadAllText(Path.Join(root, RelativePath)))!.AsObject();
        var paths = document["paths"]!.AsArray().Select(node => node!.AsObject()).Select(entry =>
        {
            var id = (string)entry["id"]!;
            var gateName = (string)entry["gate"]!;
            if (!GateNames.TryGetValue(gateName, out var gate))
            {
                throw new InvalidDataException($"{RelativePath}: path {id} has gate \"{gateName}\", which is not claim, scan, refused or exempt.");
            }

            return new FileChangingPath(id, (string)entry["what"]!, gate, (string?)entry["jobKind"], Strings(entry["routes"]), (string?)entry["because"], Strings(entry["proofs"]));
        }).ToList();
        var sites = document["sites"]!.AsArray().Select(node => node!.AsObject()).Select(entry =>
            new FileChangingSite((string)entry["file"]!, (int?)entry["calls"] ?? 0, Strings(entry["seams"]), Strings(entry["paths"]))).ToList();
        return (root, paths, sites);
    }

    private static List<string> Strings(JsonNode? node) => node is null ? [] : [.. node.AsArray().Select(item => (string)item!)];

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, RelativePath)))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"{RelativePath} was not found above the test output directory.");
    }
}
