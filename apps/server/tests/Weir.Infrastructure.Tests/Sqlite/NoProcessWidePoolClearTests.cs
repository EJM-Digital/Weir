using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Sqlite;

/// <summary>
/// Test classes run side by side in one process, and <c>SqliteConnection.ClearAllPools()</c> closes every pooled handle in it,
/// including the ones another test has just handed back and expects to be reused or is still using. A test releases only its own
/// pool, with <see cref="SqliteDatabase.ClearPool"/>.
/// </summary>
public sealed class NoProcessWidePoolClearTests
{
    private static readonly string ForbiddenCall = nameof(SqliteConnection.ClearAllPools) + "();";

    [Fact]
    public void No_test_in_this_project_clears_every_pool_in_the_process()
    {
        var folder = Path.GetFullPath(Path.Join(Path.GetDirectoryName(ThisFile())!, ".."));
        var offenders = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Equals(ThisFile(), StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains(ForbiddenCall, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(folder, path))
            .ToList();

        Assert.Empty(offenders);
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
