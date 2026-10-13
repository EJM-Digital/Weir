namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>
/// A thread that sleeps is a thread the pool cannot give to a request. The pool starts with one thread per processor and adds one only
/// every half second or so after that, so a few sleeps on request paths leave every other request waiting. The server's production source
/// has a short, named list of places that sleep; anything new has to be argued onto it here.
/// </summary>
public sealed class BlockingWaitGuardTests
{
    /// <summary>Source files allowed a <c>Thread.Sleep</c>, each with why it never holds a request's thread for long.</summary>
    private static readonly Dictionary<string, string> Sleepers = new(StringComparer.OrdinalIgnoreCase)
    {
        [Path.Join("Weir.Infrastructure", "Runtime", "ReplacedFile.cs")] = "the pause between looks at a file that is refused (never one that is not there); callers with a last good reading look once",
        [Path.Join("Weir.Infrastructure", "Runtime", "SystemServices.cs")] = "hands ReplacedFile its pause; every read on a request looks once",
        [Path.Join("Weir.Infrastructure", "Runtime", "LanAccessFile.cs")] = "hands ReplacedFile its pause; waits only for a file that is refused",
        [Path.Join("Weir.Infrastructure", "Runtime", "StartupProgress.cs")] = "start-up only, before the server listens",
        [Path.Join("Weir.Infrastructure", "Logging", "NonBlockingConsole.cs")] = "waits for the last lines when the server stops",
    };

    [Fact]
    public void Server_source_sleeps_only_in_the_places_named_here()
    {
        var root = RepositoryPaths.RepositoryRoot ?? throw new DirectoryNotFoundException("The repository root was not found above the test output directory.");
        var source = Path.Join(root, "apps", "server", "src");
        var found = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => File.ReadAllLines(file).Any(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                && line.Contains("Thread.Sleep", StringComparison.Ordinal)))
            .Select(file => Path.GetRelativePath(source, file))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(Sleepers.Keys.Order(StringComparer.OrdinalIgnoreCase), found);
    }
}
