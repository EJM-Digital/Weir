using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.E2E.Tests.Support;

/// <summary>What a rig can do to have more than one workflow: each has folders of its own, its own library folder, and its own films.</summary>
public sealed partial class LibraryRig
{
    /// <summary>
    /// The write lock on the server's database, held as a backup tool would hold it: the server keeps answering, and every change it is asked
    /// for waits until the lock is let go, which is how a test looks at a button that says it is working.
    /// </summary>
    public HeldWriteLock HoldWrites() => HeldWriteLock.Take(_server);

    /// <summary>A workflow added with <see cref="AddWorkflowAsync"/>: its id and the folder its films live in.</summary>
    public sealed record Workflow(long Id, string Name, string LibraryFolder);

    /// <summary>A movie workflow with folders of its own, set up to read its library folder.</summary>
    public async Task<Workflow> AddWorkflowAsync(string name)
    {
        var root = Directory.CreateDirectory(Path.Combine(_folders.Path, name)).FullName;
        var created = await _admin.PostWithCsrfAsync($"{Api}/processing/libraries", new JsonObject
        {
            ["name"] = name,
            ["media_type"] = "movie",
            ["watched_folder"] = Directory.CreateDirectory(Path.Combine(root, "Downloads")).FullName,
            ["output_folder"] = Directory.CreateDirectory(Path.Combine(root, "Cleaned")).FullName,
            ["minimum_free_disk_space_mb"] = 0,
        });
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        var libraryFolder = Directory.CreateDirectory(Path.Combine(root, "Films")).FullName;
        var id = (long)created.Fields["id"]!;
        var saved = await _admin.PutWithCsrfAsync(
            $"{Api}/processing/libraries/{id}/library-settings",
            new JsonObject { ["library_folders"] = new JsonArray(libraryFolder) });
        Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
        return new Workflow(id, name, libraryFolder);
    }

    /// <summary>A film with these audio languages in the workflow's library folder.</summary>
    public static async Task<string> AddFilmToAsync(Workflow workflow, string relativePath, params string[] audioLanguages)
    {
        var path = Path.Combine(workflow.LibraryFolder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: audioLanguages)));
        return path;
    }
}
