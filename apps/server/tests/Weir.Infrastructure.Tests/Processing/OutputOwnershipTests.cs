using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Configuration;
using Weir.Infrastructure.Processing;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>
/// #555: <see cref="OutputOwnership"/> against a fake <see cref="IOutputOwnershipTools"/>, so the decision logic
/// (when to chown, when to set a mode, that a tool failure is swallowed) is proven without a Linux box — the real
/// POSIX calls are <see cref="LinuxOutputOwnershipToolsRealFilesystemTests"/>, gated to Linux.
/// </summary>
public sealed class OutputOwnershipTests
{
    private sealed record ChownCall(string Path, uint Uid, uint Gid);

    private sealed record ModeCall(string Path, UnixFileMode Mode);

    private sealed class FakeTools : IOutputOwnershipTools
    {
        public readonly List<ChownCall> ChownCalls = [];
        public readonly List<ModeCall> ModeCalls = [];
        public Exception? ThrowOnChown;
        public Exception? ThrowOnSetMode;

        public void Chown(string path, uint uid, uint gid)
        {
            if (ThrowOnChown is not null)
            {
                throw ThrowOnChown;
            }

            ChownCalls.Add(new ChownCall(path, uid, gid));
        }

        public void SetMode(string path, UnixFileMode mode)
        {
            if (ThrowOnSetMode is not null)
            {
                throw ThrowOnSetMode;
            }

            ModeCalls.Add(new ModeCall(path, mode));
        }
    }

    private static WeirOptions Options(
        bool chownEnabled = false,
        uint uid = 1000,
        uint gid = 1000,
        UnixFileMode? fileMode = null,
        UnixFileMode? dirMode = null) =>
        WeirOptionsLoader.Load(new RuntimeEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["WEIR_HOME"] = Path.Join(Path.GetTempPath(), "weir-output-ownership-tests"),
                ["WEIR_CHOWN_OUTPUT"] = chownEnabled ? "true" : "false",
                ["WEIR_PUID"] = uid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["WEIR_PGID"] = gid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["WEIR_FILE_MODE_OUTPUT"] = fileMode is null ? "" : Convert.ToString((int)fileMode.Value, 8),
                ["WEIR_DIR_MODE_OUTPUT"] = dirMode is null ? "" : Convert.ToString((int)dirMode.Value, 8),
            },
            IsWindows: false,
            UserHomeDirectory: Path.GetTempPath(),
            CurrentDirectory: Path.GetTempPath()));

    [Fact]
    public void Neither_chown_nor_mode_touches_the_tools_when_nothing_is_configured()
    {
        var tools = new FakeTools();
        var ownership = new OutputOwnership(Options(), tools, NullLogger<OutputOwnership>.Instance);

        ownership.ApplyToFile("/output/movie.mkv");
        ownership.ApplyToDirectory("/output/movie");

        Assert.Empty(tools.ChownCalls);
        Assert.Empty(tools.ModeCalls);
    }

    [Fact]
    public void Chown_enabled_applies_the_configured_uid_and_gid_to_a_file()
    {
        var tools = new FakeTools();
        var ownership = new OutputOwnership(Options(chownEnabled: true, uid: 2000, gid: 2001), tools, NullLogger<OutputOwnership>.Instance);

        ownership.ApplyToFile("/output/movie.mkv");

        var call = Assert.Single(tools.ChownCalls);
        Assert.Equal(new ChownCall("/output/movie.mkv", 2000, 2001), call);
        Assert.Empty(tools.ModeCalls);
    }

    [Fact]
    public void File_mode_applies_only_to_a_file_and_directory_mode_only_to_a_directory()
    {
        var tools = new FakeTools();
        var ownership = new OutputOwnership(
            Options(fileMode: UnixFileMode.UserRead | UnixFileMode.UserWrite, dirMode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
            tools,
            NullLogger<OutputOwnership>.Instance);

        ownership.ApplyToFile("/output/movie.mkv");
        ownership.ApplyToDirectory("/output/movie");

        Assert.Equal(new ModeCall("/output/movie.mkv", UnixFileMode.UserRead | UnixFileMode.UserWrite), Assert.Single(tools.ModeCalls, c => c.Path == "/output/movie.mkv"));
        Assert.Equal(
            new ModeCall("/output/movie", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
            Assert.Single(tools.ModeCalls, c => c.Path == "/output/movie"));
        Assert.Empty(tools.ChownCalls);
    }

    [Fact]
    public void Chown_and_mode_both_apply_when_both_are_configured()
    {
        var tools = new FakeTools();
        var ownership = new OutputOwnership(
            Options(chownEnabled: true, uid: 5, gid: 6, fileMode: UnixFileMode.UserRead),
            tools,
            NullLogger<OutputOwnership>.Instance);

        ownership.ApplyToFile("/output/movie.mkv");

        Assert.Single(tools.ChownCalls);
        Assert.Single(tools.ModeCalls);
    }

    [Theory]
    [MemberData(nameof(ToolFailures))]
    public void A_tool_failure_is_logged_and_never_thrown(Exception failure)
    {
        var tools = new FakeTools { ThrowOnChown = failure };
        var ownership = new OutputOwnership(Options(chownEnabled: true), tools, NullLogger<OutputOwnership>.Instance);

        // #555: a failure applying the policy must never fail the job that just finished writing the file.
        var exception = Record.Exception(() => ownership.ApplyToFile("/output/movie.mkv"));
        Assert.Null(exception);
    }

    public static TheoryData<Exception> ToolFailures() => new()
    {
        new IOException("disk error"),
        new UnauthorizedAccessException("not permitted"),
        new PlatformNotSupportedException("no chown here"),
        // #555: "never fail the job" is unconditional, not limited to the exception types a real chown/chmod
        // could plausibly throw — an unexpected failure here still must not take the just-finished job down with it.
        new InvalidOperationException("unexpected"),
    };

    [Fact]
    public void A_mode_failure_is_also_swallowed()
    {
        var tools = new FakeTools { ThrowOnSetMode = new IOException("boom") };
        var ownership = new OutputOwnership(Options(fileMode: UnixFileMode.UserRead), tools, NullLogger<OutputOwnership>.Instance);

        var exception = Record.Exception(() => ownership.ApplyToFile("/output/movie.mkv"));
        Assert.Null(exception);
    }

    [Fact]
    public void On_Windows_there_is_no_owner_or_mode_to_set_and_asking_changes_nothing()
    {
        var tools = new WindowsOutputOwnershipTools();

        Assert.Null(Record.Exception(() => tools.Chown("/output/movie.mkv", 1000, 1000)));
        Assert.Null(Record.Exception(() => tools.SetMode("/output/movie.mkv", UnixFileMode.UserRead)));
    }
}
