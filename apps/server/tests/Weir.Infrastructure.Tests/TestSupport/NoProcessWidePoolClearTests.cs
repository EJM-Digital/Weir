using System.Runtime.CompilerServices;

namespace Weir.Infrastructure.Tests.TestSupport;

/// <summary>
/// Every test class in this assembly runs alongside others in one process, so a teardown that calls
/// <c>SqliteConnection.ClearAllPools()</c> closes other tests' live pooled handles mid-test. That made
/// <see cref="Sqlite.PooledConnectionFileIdentityTests"/> fail at random: its reopen no longer got the handle it had just
/// returned. A test releases only its own database's pool (<see cref="Weir.Infrastructure.Sqlite.SqliteDatabase.ClearPool"/>)
/// or opens with pooling off; this keeps the process-wide call out of the assembly.
/// </summary>
public sealed class NoProcessWidePoolClearTests
{
    [Fact]
    public void No_test_in_this_assembly_clears_every_SQLite_pool_in_the_process()
    {
        var offenders = Directory
            .EnumerateFiles(TestsRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(nameof(NoProcessWidePoolClearTests) + ".cs", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadLines(path).Any(CallsClearAllPools))
            .Select(path => Path.GetRelativePath(TestsRoot(), path))
            .ToList();

        Assert.Empty(offenders);
    }

    // A comment may name the call (to say why it is not made); only code counts.
    private static bool CallsClearAllPools(string line) =>
        !line.TrimStart().StartsWith("//", StringComparison.Ordinal)
        && line.Contains("SqliteConnection.ClearAllPools(", StringComparison.Ordinal);

    private static string TestsRoot([CallerFilePath] string self = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(self))!;
}
