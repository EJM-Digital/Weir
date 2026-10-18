namespace Weir.E2E.Tests.Harness;

/// <summary>Where the repository's built web app and the screenshot and failure artifacts are.</summary>
public static class RepoPaths
{
    private static readonly Lazy<string> Root = new(FindRoot);

    /// <summary>The accessibility checker the web app's own tests use, installed with its dependencies.</summary>
    public static string AxeCore => Path.Combine(Root.Value, "apps", "web", "node_modules", "axe-core", "axe.min.js");

    public static string WebDist => Path.Combine(Root.Value, "apps", "web", "dist");

    /// <summary>The replies other services recorded for the tests to replay, and the requests the web app sends (`apps/server/tests/fixtures`).</summary>
    public static string Fixtures => Path.Combine(Root.Value, "apps", "server", "tests", "fixtures");

    /// <summary>Informational screenshots, ignored by git: no pixel-exact baselines are committed.</summary>
    public static string Screenshots => Path.Combine(Root.Value, "artifacts", "screenshots");

    /// <summary>What a failed test leaves behind (<see cref="FailureArtifacts"/>), ignored by git.</summary>
    public static string Failures => Path.Combine(Root.Value, "artifacts", "e2e-failures");

    private static string FindRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "apps", "server", "Weir.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (the folder holding apps/server/Weir.slnx) is not above the test output.");
    }
}
