using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Weir.Infrastructure.Tests;

/// <summary>
/// The databases of published Weir builds, checked in under <c>apps/server/tests/fixtures/databases</c> by
/// <c>scripts/make-release-fixtures.mjs</c>: the build's own file after a short scripted session, with this PC's names scrubbed out.
/// </summary>
internal static class ReleaseDatabases
{
    /// <summary>One fixture's line in <c>manifest.json</c>.</summary>
    internal sealed record Release(string Version, string Tag, string Commit, string SchemaRevision, string Sha256, IReadOnlyDictionary<string, long> Rows);

    public static string Folder => Path.Join(RepositoryPaths.RepositoryRoot!, "apps", "server", "tests", "fixtures", "databases");

    public static IReadOnlyList<Release> All { get; } = Load();

    public static TheoryData<string> Versions()
    {
        var versions = new TheoryData<string>();
        foreach (var release in All)
        {
            versions.Add(release.Version);
        }

        return versions;
    }

    public static Release Of(string version) => All.Single(release => release.Version == version);

    /// <summary>The fixture's compressed file.</summary>
    public static string FileOf(string version) => Path.Join(Folder, $"{version}.db.gz");

    /// <summary>The SHA-256 of the compressed file, in lower-case hex.</summary>
    public static string Sha256Of(string version) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(FileOf(version))));

    /// <summary>Unpacks the fixture to <paramref name="target"/>, as the file a Weir of that version left on disk.</summary>
    public static void Unpack(string version, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var compressed = File.OpenRead(FileOf(version));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var output = File.Create(target);
        gzip.CopyTo(output);
    }

    private static List<Release> Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Join(Folder, "manifest.json")));
        return [.. document.RootElement.GetProperty("databases").EnumerateArray().Select(entry => new Release(
            entry.GetProperty("version").GetString()!,
            entry.GetProperty("tag").GetString()!,
            entry.GetProperty("commit").GetString()!,
            entry.GetProperty("schema_revision").GetString()!,
            entry.GetProperty("sha256").GetString()!,
            entry.GetProperty("rows").EnumerateObject().ToDictionary(row => row.Name, row => row.Value.GetInt64())))];
    }
}
