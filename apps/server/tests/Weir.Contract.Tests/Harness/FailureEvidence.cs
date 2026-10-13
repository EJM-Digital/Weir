namespace Weir.Contract.Tests.Harness;

/// <summary>
/// What a failed contract test leaves behind: its error, and the log and the standard output and error of every server it
/// used. Each server copies them as it is disposed, under the name of the test (or, for a server shared by a whole test class,
/// of the class), and <see cref="FailureKeepingFramework"/> deletes them again unless the test failed, so a failure that does
/// not repeat on the next run still has its evidence. They go to <c>artifacts/contract-failures/&lt;run&gt;/&lt;name&gt;/</c>
/// at the repository root, ignored by git, or to the folder in <see cref="FolderVariable"/>.
/// </summary>
public static class FailureEvidence
{
    public const string FolderVariable = "WEIR_CONTRACT_FAILURES";

    private const int UniqueIdLength = 8;

    private static readonly string RunStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly AsyncLocal<string?> Running = new();
    private static readonly Lock Folders = new();

    /// <summary>
    /// The name servers started now are kept under: the test that is running, or the test class when its shared server is being
    /// started. Null outside both, as in a harness test that runs no xunit test case of its own.
    /// </summary>
    public static string? CurrentName
    {
        get => Running.Value;
        internal set => Running.Value = value;
    }

    public static string RunFolder => Path.Combine(Root(), RunStamp);

    public static string FolderOf(string name) => Path.Combine(RunFolder, name);

    /// <summary>A test's name: its class and method, and for one row of a theory also the start of its id, so rows do not share a folder.</summary>
    public static string NameOfTest(string className, string methodName, string displayName, string uniqueId) =>
        displayName.Contains('(', StringComparison.Ordinal)
            ? $"{ShortName(className)}.{methodName}-{uniqueId[..Math.Min(UniqueIdLength, uniqueId.Length)]}"
            : $"{ShortName(className)}.{methodName}";

    /// <summary>The name of the server a test class shares between its tests.</summary>
    public static string NameOfSharedServer(string className) => $"{ShortName(className)}.shared-server";

    /// <summary>Copies what the server with data folder <paramref name="home"/> wrote under <paramref name="name"/>. Nothing here may fail the test it describes.</summary>
    internal static void Archive(string name, string home)
    {
        lock (Folders)
        {
            try
            {
                var target = Path.Combine(FolderOf(name), Path.GetFileName(home));
                foreach (var relative in new[] { "logs", "contract-logs" })
                {
                    CopyFolder(Path.Combine(home, relative), Path.Combine(target, relative));
                }

                CopyFile(Path.Combine(home, "startup-progress.json"), Path.Combine(target, "startup-progress.json"));
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                // Evidence that cannot be kept is lost; the test's own result is what matters.
                Console.Error.WriteLine($"contract harness: could not keep the logs of {home} for {name}: {problem.Message}");
            }
        }
    }

    /// <summary>Keeps the folder of <paramref name="name"/> and adds what it failed with.</summary>
    internal static void Keep(string name, string error)
    {
        lock (Folders)
        {
            var folder = FolderOf(name);
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "error.txt"), error);
        }
    }

    /// <summary>Deletes the folder of <paramref name="name"/>, which has nothing to show.</summary>
    internal static void Discard(string name)
    {
        lock (Folders)
        {
            DeleteIfExists(FolderOf(name));
            if (Directory.Exists(RunFolder) && !Directory.EnumerateFileSystemEntries(RunFolder).Any())
            {
                DeleteIfExists(RunFolder);
            }
        }
    }

    private static string Root()
    {
        if (Environment.GetEnvironmentVariable(FolderVariable) is { Length: > 0 } configured)
        {
            return configured;
        }

        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "apps", "server", "Weir.slnx")))
            {
                return Path.Combine(folder.FullName, "artifacts", "contract-failures");
            }
        }

        return Path.Combine(Path.GetTempPath(), "weir-contract-failures");
    }

    private static string ShortName(string className) => className.Split('.').Last();

    private static void CopyFolder(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(from))
        {
            CopyFile(file, Path.Combine(to, Path.GetFileName(file)));
        }
    }

    // A server may still hold its log open for writing, so the file is read with the share the writer allows.
    private static void CopyFile(string from, string to)
    {
        if (!File.Exists(from))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.Read);
        source.CopyTo(target);
    }

    private static void DeleteIfExists(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
