using System.Diagnostics;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// What a workflow's <c>rejected_file_action</c> does to a file the rules reject, on a real server with real files: Leave keeps it, Delete
/// removes that file and nothing else, and only from inside the watched folder. A workflow linked to a media manager never deletes the
/// download, and under the reject policy the manager is told and the file stays for it to remove.
/// </summary>
[ContractArea("processing")]
public sealed class RejectedFileActionTests
{
    private const string Release = "No.Audio.2023";
    private const string RelativePath = Release + "/film.mkv";

    private static byte[] NoAudio => FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: []));

    private static async Task<JsonObject> RejectAsync(Scenario scenario, JsonObject library, string relativePath = RelativePath)
    {
        await scenario.EnqueueFilePassAsync(relativePath, library);
        return await scenario.WaitForFileStatusAsync(library, relativePath, "rejected");
    }

    [Fact]
    public async Task Leave_keeps_the_file_where_it_is_and_the_row_says_so()
    {
        await using var scenario = await Scenario.StartAsync();
        var library = await scenario.CreateLibraryAsync(("failure_policy", "pass_through"), ("rejected_file_action", "leave"));
        var source = scenario.WriteRelease(Release, "film.mkv", NoAudio);

        var row = await RejectAsync(scenario, library);

        Assert.True(File.Exists(source));
        Assert.EndsWith("The file was left where it is.", (string)row["status_reason"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_file_removes_only_the_rejected_file_and_the_row_says_so()
    {
        await using var scenario = await Scenario.StartAsync();
        var library = await scenario.CreateLibraryAsync(("failure_policy", "pass_through"), ("rejected_file_action", "delete_file"));
        var source = scenario.WriteRelease(Release, "film.mkv", NoAudio);
        var sidecar = Path.Combine(Path.GetDirectoryName(source)!, "film.nfo");
        File.WriteAllText(sidecar, "metadata");
        var neighbour = scenario.WriteRelease("Other.Release.2023", "other.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng"])));

        await scenario.EnqueueFilePassAsync(RelativePath, library);
        var row = await new JobWait().UntilAsync(
            scenario.JobStatusesAsync,
            async () => await scenario.FileRowAsync(library, RelativePath) is { } found && ((string)found["status_reason"]!).EndsWith("because you chose to delete rejected files.", StringComparison.Ordinal) ? found : null,
            "the rejected file to be deleted");

        Assert.Equal("rejected", (string)row["status"]!);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(sidecar), "Only the rejected file is deleted, not what sits beside it.");
        Assert.True(File.Exists(neighbour), "Another release is not touched.");
        Assert.True(Directory.Exists(scenario.Folders.Watched), "The watched folder itself stays.");
    }

    [Fact]
    public async Task Delete_file_removes_the_release_folder_it_empties_but_never_the_watched_folder()
    {
        await using var scenario = await Scenario.StartAsync();
        var library = await scenario.CreateLibraryAsync(("failure_policy", "pass_through"), ("rejected_file_action", "delete_file"));
        var source = scenario.WriteRelease(Release, "film.mkv", NoAudio);

        await scenario.EnqueueFilePassAsync(RelativePath, library);
        await new JobWait().UntilAsync(
            scenario.JobStatusesAsync,
            async () => !File.Exists(source) && await scenario.FileRowAsync(library, RelativePath) is { } found && (string)found["status"]! == "rejected" ? found : null,
            "the rejected file to be deleted");

        Assert.False(Directory.Exists(Path.GetDirectoryName(source)!));
        Assert.True(Directory.Exists(scenario.Folders.Watched));
    }

    [Fact]
    public async Task Delete_file_never_reaches_a_file_outside_the_watched_folder()
    {
        await using var scenario = await Scenario.StartAsync();
        var library = await scenario.CreateLibraryAsync(("failure_policy", "pass_through"), ("rejected_file_action", "delete_file"));
        var elsewhere = Directory.CreateDirectory(Path.Combine(scenario.Root, "elsewhere", Release)).FullName;
        var outside = Path.Combine(elsewhere, "film.mkv");
        await File.WriteAllBytesAsync(outside, NoAudio);
        var link = Path.Combine(scenario.Folders.Watched, Release);
        await FolderLink.CreateAsync(link, elsewhere);
        try
        {
            await scenario.EnqueueFilePassAsync(RelativePath, library);
            await new JobWait().UntilAsync(
                scenario.JobStatusesAsync,
                async () => await scenario.FileRowAsync(library, RelativePath) is { } found && ((string)found["status_reason"]!).Contains("did not delete the rejected file", StringComparison.Ordinal) ? found : null,
                "the refusal to delete the file to be recorded");

            Assert.True(File.Exists(outside), "A file that lives outside the watched folder is never deleted.");
        }
        finally
        {
            // Removes the link itself; the scenario's folder is then cleared without following it.
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task A_workflow_linked_to_a_manager_never_deletes_the_download_whatever_its_action_says()
    {
        await using var scenario = await Scenario.StartAsync();
        var (_, library) = await scenario.RadarrSetupAsync(("failure_policy", "pass_through"), ("rejected_file_action", "delete_file"));
        var source = scenario.WriteRelease(Release, "film.mkv", NoAudio);

        var row = await RejectAsync(scenario, library);

        Assert.True(File.Exists(source));
        Assert.Contains(
            "The file was left where it is. This workflow is linked to Radarr, so the original stays with your download client, which may still be seeding.",
            (string)row["status_reason"]!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Under_the_reject_policy_the_manager_is_told_and_the_file_is_left_for_it_to_remove()
    {
        await using var scenario = await Scenario.StartAsync();
        var (fake, _) = await scenario.DelunoSetupAsync(
            capabilities: ["processor-reject-regrab"], library: [("failure_policy", "reject"), ("rejected_file_action", "delete_file")]);
        var source = scenario.WriteRelease(Release, "film.mkv", NoAudio);

        await scenario.PostHandoffAsync("handoff-reject-action-1", source);
        await scenario.WaitForHandoffStateAsync("handoff-reject-action-1", "rejected");

        var report = Assert.Single(Scenario.Callbacks(fake, "handoff-reject-action-1"));
        Assert.Equal("rejected", (string)report["disposition"]!);
        Assert.False((bool)report["sourceRemoved"]!);
        Assert.True(File.Exists(source), "The download is the manager's to remove, so Weir leaves it.");
        var rejected = Assert.Single(await scenario.ActivityAsync("processing.file_rejected"));
        Assert.Contains("will remove the download; Weir left it in place.", (string)rejected["detail"]!, StringComparison.Ordinal);
    }

    /// <summary>A folder that is a link to another: a directory junction on Windows, which needs no privilege, or a symbolic link elsewhere.</summary>
    private static class FolderLink
    {
        public static async Task CreateAsync(string link, string target)
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(link, target);
                return;
            }

            var start = new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, target }, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, $"could not link {link} to {target}: {await error}");
        }
    }
}
