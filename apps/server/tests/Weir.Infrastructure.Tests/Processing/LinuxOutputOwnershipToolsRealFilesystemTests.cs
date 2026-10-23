using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Weir.Infrastructure.Processing;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>A fact that runs only on Linux, where chown(2)/chmod bits actually exist.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresLinuxFactAttribute : FactAttribute
{
    public RequiresLinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "LinuxOutputOwnershipTools calls real chown(2)/chmod bits, which only exist on Linux.";
        }
    }
}

/// <summary>
/// #555: <see cref="LinuxOutputOwnershipTools"/> against real files. The fake-based <see cref="OutputOwnershipTests"/>
/// covers the decision logic on every OS; this proves the P/Invoke chown and <c>File.SetUnixFileMode</c> calls
/// actually work on the real filesystem. Gated to Linux (skipped elsewhere with a reason) since neither is available
/// off it, and a chown to an arbitrary uid needs root, so this only ever chowns to the running process's own ids —
/// an unprivileged no-op chown that still exercises the real syscall.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxOutputOwnershipToolsRealFilesystemTests : IDisposable
{
    private readonly string _folder = Path.Join(Path.GetTempPath(), "weir-output-ownership-tests-" + Guid.NewGuid().ToString("N"));

    public LinuxOutputOwnershipToolsRealFilesystemTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [RequiresLinuxFact]
    public void SetMode_changes_a_real_files_permission_bits()
    {
        var path = Path.Join(_folder, "output.mkv");
        File.WriteAllText(path, "content");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);

        new LinuxOutputOwnershipTools().SetMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [RequiresLinuxFact]
    public void SetMode_changes_a_real_directorys_permission_bits()
    {
        var path = Path.Join(_folder, "output");
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                   UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                   UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        new LinuxOutputOwnershipTools().SetMode(path, mode);

        Assert.Equal(mode, File.GetUnixFileMode(path));
    }

    [RequiresLinuxFact]
    public void Chown_to_the_running_processs_own_ids_succeeds_without_root()
    {
        var path = Path.Join(_folder, "output.mkv");
        File.WriteAllText(path, "content");

        // An unprivileged chown to a file's own current owner is always permitted — this proves the P/Invoke plumbing
        // (argument marshalling, errno handling) without needing root or a second real user to chown to.
        var exception = Record.Exception(() => new LinuxOutputOwnershipTools().Chown(path, Geteuid(), Getegid()));
        Assert.Null(exception);
    }

    [RequiresLinuxFact]
    public void Chown_of_a_path_that_is_not_there_is_an_error_that_names_it()
    {
        var path = Path.Join(_folder, "missing.mkv");

        var failure = Assert.Throws<IOException>(() => new LinuxOutputOwnershipTools().Chown(path, Geteuid(), Getegid()));

        Assert.Contains(path, failure.Message, StringComparison.Ordinal);
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint Geteuid();

    [DllImport("libc", EntryPoint = "getegid")]
    private static extern uint Getegid();
}

/// <summary>The Linux tools are never used on Windows, and say so if they are.</summary>
public sealed class LinuxOutputOwnershipToolsOnWindowsTests
{
    [WindowsFact("Only Windows refuses to set a mode")]
    public void Setting_a_mode_on_Windows_is_refused()
    {
        Assert.Throws<PlatformNotSupportedException>(() => new LinuxOutputOwnershipTools().SetMode(Path.GetTempPath(), UnixFileMode.UserRead));
    }
}
