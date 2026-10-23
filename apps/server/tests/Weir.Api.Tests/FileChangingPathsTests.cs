using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Core.Jobs;
using Weir.Tests.Shared;

namespace Weir.Api.Tests;

/// <summary>
/// While processing is paused nothing changes a media file. <c>docs/coverage/file-changing-paths.json</c> lists every way Weir can change
/// one and what holds it back, and these tests keep that list whole against the server itself: the job kinds it registers, the routes
/// it maps, and a scan of its source for the calls that change files. A new job kind, route or call that is not classified fails here,
/// so a path cannot reach a media file without someone deciding what the pause does to it.
/// </summary>
public sealed partial class FileChangingPathsTests
{
    /// <summary>Calls that change a file or folder directly, in the .NET file API or on Weir's swap file system.</summary>
    private static readonly Regex[] FileChangingCalls =
    [
        DirectFileCall(),
        DirectFolderCall(),
        StreamThatWrites(),
        InstanceMove(),
        InstanceDelete(),
        SwapFileSystemCall(),
        FileSystemSeamCall(),
        FileSystemSeamStreamThatWrites(),
        NativeCall(),
    ];

    /// <summary>
    /// Helpers that change files, or ask a media manager to, for the work that calls them. Each is a call a file must be listed for, so
    /// a new caller of one is classified even though it makes no file call of its own.
    /// </summary>
    private static readonly (string Name, Regex Call)[] Seams =
    [
        ("FileLifecycle.SafeCopyToFinalAsync", new Regex(@"\.SafeCopyToFinalAsync\s*\(")),
        ("FileLifecycle.SafeFinalizeFile", new Regex(@"\.SafeFinalizeFile\s*\(")),
        ("FileLifecycle.TryHardlinkToFinalAsync", new Regex(@"\.TryHardlinkToFinalAsync\s*\(")),
        ("HandbackStore.Release", new Regex(@"\bHandbackStore\.Release\s*\(")),
        ("IManagerRedownload.RequestRedownloadAsync", new Regex(@"\.RequestRedownloadAsync\s*\(")),
        ("IMediaManagerPort.RemoveQueueItemAsync", new Regex(@"\.RemoveQueueItemAsync\s*\(")),
        ("MediaTools.RemuxToTempFileAsync", new Regex(@"\.RemuxToTempFileAsync\s*\(")),
        ("OriginalsMover", new Regex(@"\bOriginalsMover\.(Reserve|Fill|Recover|CleanUpUnfilledReservation)\s*\(")),
        ("OutputFolderCleanup.CascadeDeleteEmptyParents", new Regex(@"\.CascadeDeleteEmptyParents\s*\(")),
        ("OutputFolderCleanup.Run", new Regex(@"\.(RunMovieAsync|RunTvAsync)\s*\(")),
        ("ReconciliationService.RepairAsync", new Regex(@"\bReconciliationService\.RepairAsync\s*\(")),
        ("RejectRoutes.Through", new Regex(@"\.(ThroughQueueAsync|ThroughHandoffAsync)\s*\(")),
        ("ReleaseFolderRemoval.Remove", new Regex(@"\bReleaseFolderRemoval\.Remove\s*\(")),
        ("RemuxPassPaths.CleanupRejectedFile", new Regex(@"\.CleanupRejectedFile\s*\(")),
        ("SafeSwap.RunAsync", new Regex(@"\b_swap\.RunAsync\s*\(")),
        ("SwapRecoverySweep.RecoverFile", new Regex(@"\.RecoverFile\s*\(")),
        ("SwapRecoverySweep.RunAsync", new Regex(@"\b_swapSweep\.RunAsync\s*\(")),
        ("TvSeasonFolderCleanup.RunAsync", new Regex(@"\b_tvSeasonCleanup\.RunAsync\s*\(")),
    ];

    [Fact]
    public async Task The_server_registers_exactly_the_job_kinds_the_list_names()
    {
        await using var server = await WeirTestServer.StartAsync();

        var registered = server.Services.GetRequiredService<JobHandlerRegistry>().JobKinds.Order(StringComparer.Ordinal).ToList();
        var listed = FileChangingPathsTable.Paths.Where(path => path.JobKind is not null).Select(path => path.JobKind!).Order(StringComparer.Ordinal).ToList();

        var unlisted = registered.Except(listed).ToList();
        var gone = listed.Except(registered).ToList();
        Assert.True(
            unlisted.Count == 0,
            "The server registers job kinds docs/coverage/file-changing-paths.json does not list; add each with how the pause treats it: " + string.Join(", ", unlisted));
        Assert.True(gone.Count == 0, "docs/coverage/file-changing-paths.json lists job kinds the server no longer registers; remove them: " + string.Join(", ", gone));
    }

    [Fact]
    public async Task Only_the_scan_that_keeps_looking_for_files_is_claimed_through_a_pause()
    {
        await using var server = await WeirTestServer.StartAsync();

        var registered = server.Services.GetRequiredService<JobHandlerRegistry>().JobKinds;
        var runsWhilePaused = registered.Where(WorkAdmissionRules.IsDetectionJobKind).Order(StringComparer.Ordinal).ToList();
        var listedAsScan = FileChangingPathsTable.Paths.Where(path => path.Gate == PauseGate.Scan).Select(path => path.JobKind!).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(listedAsScan, runsWhilePaused);
    }

    [Fact]
    public async Task Every_route_the_list_names_is_one_the_server_maps()
    {
        await using var server = await WeirTestServer.StartAsync();

        var mapped = server.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(method => $"{method.ToUpperInvariant()} {endpoint.RoutePattern.RawText}"))
            .ToHashSet(StringComparer.Ordinal);

        var gone = FileChangingPathsTable.Paths.SelectMany(path => path.Routes).Where(route => !mapped.Contains(route)).ToList();
        Assert.True(gone.Count == 0, "docs/coverage/file-changing-paths.json names routes the server does not map: " + string.Join(", ", gone));
    }

    [Fact]
    public void Every_file_that_changes_files_or_calls_a_helper_that_does_is_listed_with_what_it_serves()
    {
        var found = ScanServerSource();
        var listed = FileChangingPathsTable.Sites.ToDictionary(site => site.File, StringComparer.OrdinalIgnoreCase);

        var problems = new List<string>();
        foreach (var (file, (calls, seams)) in found.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!listed.TryGetValue(file, out var site))
            {
                problems.Add($"{file} is not in docs/coverage/file-changing-paths.json; it has {calls} file-changing calls and calls {(seams.Count == 0 ? "no helper" : string.Join(", ", seams))}. Add it with the path it serves.");
                continue;
            }

            if (site.Calls != calls)
            {
                problems.Add($"{file} has {calls} file-changing calls and the list says {site.Calls}. Decide what the new call serves, then update the list.");
            }

            if (!site.Seams.Order(StringComparer.Ordinal).SequenceEqual(seams.Order(StringComparer.Ordinal)))
            {
                problems.Add($"{file} calls {Describe(seams)} and the list says {Describe(site.Seams)}. Decide what the new call serves, then update the list.");
            }
        }

        problems.AddRange(listed.Keys.Where(file => !found.ContainsKey(file)).Select(file => $"{file} is in the list but no longer changes files or calls a helper that does; remove it."));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_path_says_how_the_pause_treats_it_and_every_site_names_paths_that_exist()
    {
        var paths = FileChangingPathsTable.Paths;
        var ids = paths.Select(path => path.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        var problems = new List<string>();
        foreach (var path in paths)
        {
            switch (path.Gate)
            {
                case PauseGate.Claim or PauseGate.Scan when path.JobKind is null:
                    problems.Add($"{path.Id} is held by the worker claim, so it needs the job kind it is claimed as.");
                    break;
                case PauseGate.Scan when path.Proofs.Count == 0:
                    problems.Add($"{path.Id} runs while paused, so it needs the tests that show it changes no file.");
                    break;
                case PauseGate.Refused when path.Routes.Count == 0 || path.Proofs.Count == 0:
                    problems.Add($"{path.Id} is refused while paused, so it needs the routes it is refused on and the tests that show it.");
                    break;
                case PauseGate.Exempt when string.IsNullOrWhiteSpace(path.Because):
                    problems.Add($"{path.Id} is exempt from the pause, so it needs the reason why.");
                    break;
            }

            problems.AddRange(path.Proofs.Where(proof => !ProofExists(proof)).Select(proof => $"{path.Id} names a test that does not exist: {proof}"));
        }

        foreach (var site in FileChangingPathsTable.Sites)
        {
            problems.AddRange(site.Paths.Where(id => !ids.Contains(id)).Select(id => $"{site.File} serves {id}, which is not a path in the list."));
            if (site.Paths.Count == 0)
            {
                problems.Add($"{site.File} serves no path.");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static string Describe(IEnumerable<string> seams) => seams.Any() ? string.Join(", ", seams.Order(StringComparer.Ordinal)) : "no helper";

    /// <summary>Whether <c>path/to/Tests.cs::Test_name</c> names a test method that is in that file.</summary>
    private static bool ProofExists(string proof)
    {
        var parts = proof.Split("::");
        var file = Path.Join(FileChangingPathsTable.RepositoryRoot, parts[0]);
        return parts.Length == 2 && File.Exists(file) && Regex.IsMatch(File.ReadAllText(file), @"\b" + Regex.Escape(parts[1]) + @"\s*\(");
    }

    /// <summary>For each production source file that changes files or calls a helper that does: how many direct calls, and which helpers.</summary>
    private static Dictionary<string, (int Calls, List<string> Seams)> ScanServerSource()
    {
        var source = Path.Join(FileChangingPathsTable.RepositoryRoot, "apps", "server", "src");
        var found = new Dictionary<string, (int, List<string>)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (relative.Split('/').Any(part => part is "obj" or "bin"))
            {
                continue;
            }

            // The real calls live in one place, the pass-through every other file reaches the disk by; what is counted is who calls it.
            if (relative == "Weir.Infrastructure/IO/IFileSystem.cs")
            {
                continue;
            }

            var text = WithoutComments(File.ReadAllText(file));
            var calls = FileChangingCalls.Sum(pattern => pattern.Count(text));
            var seams = Seams.Where(seam => seam.Call.IsMatch(text)).Select(seam => seam.Name).ToList();
            if (calls > 0 || seams.Count > 0)
            {
                found[relative] = (calls, seams);
            }
        }

        return found;
    }

    private static string WithoutComments(string text) =>
        string.Join('\n', BlockComment().Replace(text, string.Empty).Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    [GeneratedRegex(@"\bFile\.(Delete|Move|Copy|Replace|WriteAll\w*|AppendAll\w*|Create|OpenWrite|SetLastWrite\w*|SetCreationTime\w*|SetAttributes|SetUnixFileMode|CreateSymbolicLink|Encrypt|Decrypt)\s*\(")]
    private static partial Regex DirectFileCall();

    [GeneratedRegex(@"\bDirectory\.(Delete|Move)\s*\(")]
    private static partial Regex DirectFolderCall();

    [GeneratedRegex(@"\bnew\s+FileStream\s*\([^;]*?FileMode\.(Create|CreateNew|Append|OpenOrCreate|Truncate)", RegexOptions.Singleline)]
    private static partial Regex StreamThatWrites();

    [GeneratedRegex(@"\.(MoveTo)\s*\(")]
    private static partial Regex InstanceMove();

    [GeneratedRegex(@"\b\w+\.Delete\(\s*\)")]
    private static partial Regex InstanceDelete();

    [GeneratedRegex(@"\b_?files\.(Move|Delete|ReplaceReservation|CopyOverReservation|TryReserve)\s*\(")]
    private static partial Regex SwapFileSystemCall();

    [GeneratedRegex(@"\b(_?files|_fs|FileSystem|_?fileSystem)\.(DeleteFile|DeleteDirectory|MoveFile|CopyFile|WriteAllBytes|SetLastWriteTimeUtc|SetLastAccessTimeUtc)\s*\(")]
    private static partial Regex FileSystemSeamCall();

    [GeneratedRegex(@"\b(_?files|_fs|FileSystem|_?fileSystem)\.OpenFile\s*\([^;]*?FileMode\.(Create|CreateNew|Append|OpenOrCreate|Truncate)", RegexOptions.Singleline)]
    private static partial Regex FileSystemSeamStreamThatWrites();

    [GeneratedRegex(@"\b(MoveFileEx\w*|CreateHardLink\w*)\s*\(")]
    private static partial Regex NativeCall();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();
}
