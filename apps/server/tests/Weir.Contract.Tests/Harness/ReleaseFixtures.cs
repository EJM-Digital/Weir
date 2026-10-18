using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weir.Contract.Tests.Harness;

/// <summary>
/// What each published release left behind (<c>fixtures/databases/README.md</c>): the database its image wrote after a short scripted
/// session, and the configuration backup it made. The tests start the current server on the database and restore the backup.
/// </summary>
internal static class ReleaseFixtures
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

    /// <summary>Puts the release's database where a server of <paramref name="server"/> keeps its own, in place of what is there. The server must be stopped.</summary>
    public static void PlaceDatabase(string version, WeirServer server)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(server.DatabasePath + suffix);
        }

        using var compressed = File.OpenRead(Path.Combine(Folder, "databases", $"{version}.db.gz"));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var output = File.Create(server.DatabasePath);
        gzip.CopyTo(output);
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
