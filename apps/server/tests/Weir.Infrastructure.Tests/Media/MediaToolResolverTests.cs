using Weir.Core.Media;
using Weir.Infrastructure.Media;

namespace Weir.Infrastructure.Tests.Media;

/// <summary>
/// Where Weir looks for ffprobe, ffmpeg and mkvmerge, on real folders holding real files: the folder the owner names, the packaged app's
/// folders, then PATH, and what is said when none holds them.
/// </summary>
public sealed class MediaToolResolverTests : IDisposable
{
    private static readonly bool Windows = OperatingSystem.IsWindows();

    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    private string Folder(params string[] parts) => Directory.CreateDirectory(_root.Join(parts)).FullName;

    private static string Tool(string folder, string name, bool executable = true)
    {
        var path = Path.Join(folder, name);
        File.WriteAllText(path, "tool");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, executable ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return path;
    }

    private static (string Ffprobe, string Ffmpeg) FfNames => MediaToolLocations.ToolNames(Windows);

    private static (string Ffprobe, string Ffmpeg) Both(string folder, bool executable = true) =>
        (Tool(folder, FfNames.Ffprobe, executable), Tool(folder, FfNames.Ffmpeg, executable));

    private static MediaToolResolver Resolver(string? app = null, string? ffmpegDir = null, string? mkvDir = null, params string[] path) =>
        new(app, name => name switch
        {
            MediaToolLocations.FfmpegDirEnvironmentVariable => ffmpegDir,
            MediaToolLocations.MkvtoolnixDirEnvironmentVariable => mkvDir,
            "PATH" => string.Join(Path.PathSeparator, path),
            _ => null,
        });

    [Fact]
    public void The_folder_the_owner_names_is_used_before_the_packaged_folders_and_PATH()
    {
        var named = Both(Folder("named"));
        Both(Folder("app", "bin", "ffmpeg"));
        Both(Folder("onpath"));

        var found = Resolver(_root.Join("app"), _root.Join("named"), null, _root.Join("onpath")).Resolve();

        Assert.Equal(named, found);
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("_internal")]
    public void The_tools_packaged_beside_the_app_are_used_when_no_folder_is_named(string layout)
    {
        var packaged = layout == "bin" ? Folder("app", "bin", "ffmpeg") : Folder("app", "_internal", "bin", "ffmpeg");
        var tools = Both(packaged);
        Both(Folder("onpath"));

        Assert.Equal(tools, Resolver(_root.Join("app"), null, null, _root.Join("onpath")).Resolve());
    }

    [Fact]
    public void A_folder_holding_only_one_of_the_two_tools_is_passed_over()
    {
        Tool(Folder("half"), FfNames.Ffprobe);
        var onPath = Both(Folder("onpath"));

        Assert.Equal(onPath, Resolver(null, _root.Join("half"), null, _root.Join("onpath")).Resolve());
    }

    [Fact]
    public void PATH_is_searched_in_order_and_a_folder_listed_twice_or_left_empty_is_harmless()
    {
        Both(Folder("first"), executable: true);
        Both(Folder("second"));

        var found = Resolver(null, null, null, string.Empty, _root.Join("first"), _root.Join("first"), _root.Join("second")).Resolve();

        Assert.Equal(Path.Join(_root.Join("first"), FfNames.Ffprobe), found.Ffprobe);
    }

    [Fact]
    public void No_tools_anywhere_says_what_to_do_in_plain_words()
    {
        Tool(Folder("half"), FfNames.Ffmpeg);

        var failure = Assert.Throws<MediaToolException>(() => Resolver(_root.Join("none"), _root.Join("half"), null, _root.Join("half")).Resolve());

        Assert.Equal(MediaToolLocations.MissingToolsMessage, failure.Message);
    }

    [PosixFact("A file's execute permission only exists off Windows.")]
    public void A_file_on_PATH_that_may_not_be_run_is_not_a_tool()
    {
        Both(Folder("locked"), executable: false);

        Assert.Throws<MediaToolException>(() => Resolver(null, null, null, _root.Join("locked")).Resolve());
    }

    [Fact]
    public void A_folder_on_PATH_that_does_not_exist_is_skipped()
    {
        var onPath = Both(Folder("onpath"));

        Assert.Equal(onPath, Resolver(null, null, null, _root.Join("missing"), _root.Join("onpath")).Resolve());
    }

    [Fact]
    public void Mkvmerge_is_found_in_the_named_folder_then_beside_the_app_then_on_PATH()
    {
        var name = MediaToolLocations.MkvmergeToolName(Windows);
        var named = Tool(Folder("named"), name);
        var packaged = Tool(Folder("app", "bin", "mkvtoolnix"), name);
        var onPath = Tool(Folder("onpath"), name);
        var path = _root.Join("onpath");

        Assert.Equal(named, Resolver(_root.Join("app"), null, _root.Join("named"), path).ResolveMkvmerge());
        Assert.Equal(packaged, Resolver(_root.Join("app"), null, null, path).ResolveMkvmerge());
        Assert.Equal(onPath, Resolver(null, null, _root.Join("empty"), path).ResolveMkvmerge());
    }

    [Fact]
    public void Mkvmerge_nowhere_is_nothing_rather_than_a_failure()
    {
        Assert.Null(Resolver(_root.Join("app"), null, _root.Join("empty"), _root.Join("empty")).ResolveMkvmerge());
    }

    [PosixFact("A file's execute permission only exists off Windows.")]
    public void Mkvmerge_on_PATH_that_may_not_be_run_is_not_found()
    {
        Tool(Folder("locked"), MediaToolLocations.MkvmergeToolName(Windows), executable: false);

        Assert.Null(Resolver(null, null, null, _root.Join("locked")).ResolveMkvmerge());
    }
}
