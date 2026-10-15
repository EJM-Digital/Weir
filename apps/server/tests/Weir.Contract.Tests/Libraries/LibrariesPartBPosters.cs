using System.Net;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Libraries;

/// <summary>Files Weir knows about and the poster addresses it shows for them.</summary>
internal static class LibrariesPartBPosters
{
    public const string PosterPrefix = $"{WeirClient.Api}/artwork/posters/";

    /// <summary>A file Weir knows (and, when asked, an Activity entry about it), written while the server is stopped.</summary>
    public static Task SeedFileAsync(WeirServer server, string path, string mediaType = "movie", bool activity = false) =>
        SeedFilesAsync(server, [path], mediaType, activity);

    /// <summary>
    /// Files Weir knows, all written in one stop of the server. Every stop restarts the server, and a restarted server looks titles up
    /// within seconds; a lookup the stop cuts short is asked again afterwards. A test that counts lookups therefore seeds the files
    /// it counts together, so no lookup runs between two of them.
    /// </summary>
    public static async Task SeedFilesAsync(WeirServer server, IEnumerable<string> paths, string mediaType = "movie", bool activity = false)
    {
        await using var database = await server.StopForDatabaseAsync();
        var connection = database.Connection;
        var libraryId = SeedSql.Scalar(
            connection, "SELECT id FROM libraries WHERE media_type = $mediaType ORDER BY id LIMIT 1", ("$mediaType", mediaType));
        var now = SeedSql.UtcText(DateTime.UtcNow);
        foreach (var path in paths)
        {
            SeedSql.Execute(
                connection,
                "INSERT INTO files (library_id, relative_path, status, last_seen_at) VALUES ($libraryId, $path, 'processed', $now)",
                ("$libraryId", libraryId), ("$path", path), ("$now", now));
            if (activity)
            {
                SeedSql.Execute(
                    connection,
                    "INSERT INTO activity_events (event_type, module, title, library_id, relative_path, created_at) "
                        + "VALUES ('processing.file_remux_pass_completed', 'processing', 'Finished', $libraryId, $path, $now)",
                    ("$libraryId", libraryId), ("$path", path), ("$now", now));
            }
        }
    }

    /// <summary>The poster address the files list gives <paramref name="path"/>, or null when it carries none; fails when the file is not listed.</summary>
    public static async Task<string?> PosterUrlOfAsync(WeirClient client, string path)
    {
        var listed = await client.GetAsync($"{WeirClient.Api}/processing/files");
        LibrariesPartBChecks.Status(listed, HttpStatusCode.OK);
        foreach (var node in listed.Fields["files"]!.AsArray())
        {
            var entry = node!.AsObject();
            if ((string?)entry["relative_path"] == path)
            {
                Assert.True(entry.ContainsKey("poster_url"), entry.ToJsonString());
                return (string?)entry["poster_url"];
            }
        }

        throw new Xunit.Sdk.XunitException($"{path} is not in the files list");
    }

    public static Task<string> WaitForPosterAsync(WeirClient client, string path) => Poll.UntilAsync(
        async () => await PosterUrlOfAsync(client, path) is { Length: > 0 } url ? url : null,
        $"a poster address for {path}");

    /// <summary>The search a title lookup sent: the media type, the lowercase title and (for a film) its year.</summary>
    public static void AssertSearch(RecordedRequest search, string mediaType, string title, string? year)
    {
        var expected = new Dictionary<string, string[]> { ["mediaType"] = [mediaType], ["query"] = [title] };
        if (year is not null)
        {
            expected["year"] = [year];
        }

        Assert.Equal(expected.Count, search.Query.Count);
        foreach (var (name, values) in expected)
        {
            Assert.Equal(values, search.Query[name]);
        }
    }
}
