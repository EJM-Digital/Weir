using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Weir.Contract.Tests.Harness;

/// <summary>
/// What each published release left behind (<c>fixtures/databases/README.md</c>): the database its image wrote after a short scripted
/// session, and the configuration backup it made. The tests start the current server on the database and restore the backup.
/// </summary>
internal static partial class ReleaseFixtures
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "fixtures");

    /// <summary>The releases that have fixtures, oldest first.</summary>
    public static IReadOnlyList<string> All { get; } = Load();

    public static TheoryData<string> Versions()
    {
        var versions = new TheoryData<string>();
        foreach (var version in All)
        {
            versions.Add(version);
        }

        return versions;
    }

    /// <summary>Puts the release's database where a server of <paramref name="server"/> keeps its own, in place of what is there. The server must be stopped; this waits until the operating system has let go of the files it held.</summary>
    public static async Task PlaceDatabaseAsync(string version, WeirServer server)
    {
        await StoppedDatabase.WaitForReleaseAsync(server.DatabasePath);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(server.DatabasePath + suffix);
        }

        using var compressed = File.OpenRead(Path.Combine(Folder, "databases", $"{version}.db.gz"));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var output = File.Create(server.DatabasePath);
        gzip.CopyTo(output);
    }

    /// <summary>Whether the release wrote a schema older than the one <paramref name="server"/> created on its first start, which is what makes an update migrate and save a copy first.</summary>
    public static bool MigratesOnStart(string version, WeirServer server)
    {
        var head = CreatedRevision().Match(server.LogText());
        Assert.True(head.Success, "the server's first start should log the schema revision it created");
        return string.CompareOrdinal(Manifest(version).GetProperty("schema_revision").GetString(), head.Groups[1].Value) < 0;
    }

    [GeneratedRegex(@"Created database schema revision=(\S+)")]
    private static partial Regex CreatedRevision();

    private static JsonElement Manifest(string version)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "databases", "manifest.json")));
        return manifest.RootElement.GetProperty("databases").EnumerateArray().Single(entry => entry.GetProperty("version").GetString() == version).Clone();
    }

    /// <summary>The configuration backup the release made.</summary>
    public static JsonObject Backup(string version) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "recorded", "backups", version, "configuration-bundle.json")))!.AsObject();

    private static List<string> Load()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "databases", "manifest.json")));
        return [.. manifest.RootElement.GetProperty("databases").EnumerateArray().Select(entry => entry.GetProperty("version").GetString()!)];
    }
}
