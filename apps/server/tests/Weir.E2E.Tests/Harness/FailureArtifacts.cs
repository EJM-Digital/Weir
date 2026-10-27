using System.Reflection;
using Xunit.Sdk;

[assembly: Weir.E2E.Tests.Harness.NamesCurrentTest]

namespace Weir.E2E.Tests.Harness;

/// <summary>
/// What a failed browser test leaves behind: its full error, a screenshot of every page it had open, the Playwright trace
/// (open it with <c>playwright show-trace</c>), what the browser logged and the server's own log. Each test writes them as it
/// ends and <see cref="FailureKeepingFramework"/> deletes them again unless the test failed, so a failure that does not
/// repeat on the next run still has its evidence. They go to <c>artifacts/e2e-failures/&lt;run&gt;/&lt;test&gt;/</c>, ignored by git.
/// </summary>
public static class FailureArtifacts
{
    private static readonly string RunStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The test running now, as <see cref="KeyOf"/> names it. Every browser test is in one collection, so one runs at a time.</summary>
    public static string? CurrentTest { get; internal set; }

    public static string KeyOf(string className, string methodName) => $"{className}.{methodName}";

    public static string RunFolder => Path.Combine(RepoPaths.Failures, RunStamp);

    public static string FolderOf(string testKey) => Path.Combine(RunFolder, testKey);

    /// <summary>Keeps the test's folder and adds the error it failed with.</summary>
    internal static void Keep(string testKey, string error)
    {
        var folder = FolderOf(testKey);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "error.txt"), error);
        File.WriteAllText(Path.Combine(folder, "sockets.txt"), SocketEvidence.Describe());
    }

    /// <summary>Deletes the test's folder, which has nothing to show.</summary>
    internal static void Discard(string testKey)
    {
        DeleteIfExists(FolderOf(testKey));
        if (Directory.Exists(RunFolder) && !Directory.EnumerateFileSystemEntries(RunFolder).Any())
        {
            DeleteIfExists(RunFolder);
        }
    }

    private static void DeleteIfExists(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

/// <summary>Tells <see cref="FailureArtifacts"/> which test is running, so a test's evidence is named for it.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class NamesCurrentTestAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest) =>
        FailureArtifacts.CurrentTest = FailureArtifacts.KeyOf(methodUnderTest.DeclaringType!.Name, methodUnderTest.Name);
}
