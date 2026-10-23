using Weir.Core.Media;
using Weir.Infrastructure.IO;

namespace Weir.Infrastructure.Media;

/// <summary>Finds ffprobe and ffmpeg, and mkvmerge when it is installed.</summary>
public interface IMediaToolResolver
{
    /// <summary>(ffprobe, ffmpeg) paths. Throws <see cref="MediaToolException"/> when either is missing.</summary>
    (string Ffprobe, string Ffmpeg) Resolve();

    /// <summary>
    /// #548: the mkvmerge path, or null when it is not installed. Unlike ffprobe and ffmpeg this is optional —
    /// Weir runs perfectly well without it, writing every container with ffmpeg — so a missing mkvmerge is a
    /// null rather than a <see cref="MediaToolException"/>, and the writer setting falls back accordingly.
    /// </summary>
    string? ResolveMkvmerge();
}

/// <summary>
/// Prefers <c>WEIR_FFMPEG_DIR</c>, then (packaged builds only) the tools bundled next to the executable, then
/// PATH. Weir's data folder (<c>WEIR_HOME</c>) is never searched, because it is writable by every local
/// account on a default Windows install, so a program planted there could never be trusted over Weir's own
/// bundle. The environment is read on every call, so a tool installed or a variable changed while the server
/// runs is picked up without a restart.
/// </summary>
public sealed class MediaToolResolver : IMediaToolResolver
{
    private readonly string? _packagedAppDirectory;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly bool _windows;
    private readonly IFileSystem _files;

    /// <param name="packagedAppDirectory">The packaged app's directory, or null when not running packaged.</param>
    /// <param name="getEnvironmentVariable">Environment lookup; <see cref="Environment.GetEnvironmentVariable(string)"/> when null.</param>
    /// <param name="files">The file system the tools are looked for in; the real one when null.</param>
    public MediaToolResolver(string? packagedAppDirectory = null, Func<string, string?>? getEnvironmentVariable = null, IFileSystem? files = null)
    {
        _packagedAppDirectory = packagedAppDirectory;
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _windows = OperatingSystem.IsWindows();
        _files = files ?? PhysicalFileSystem.Instance;
    }

    /// <summary>
    /// The resolver for this process: a single-file publish (the Windows package and the Docker image) also looks in <c>&lt;app&gt;/bin/ffmpeg</c>, where the Windows package
    /// bundles ffmpeg and ffprobe.
    /// </summary>
    public static MediaToolResolver ForCurrentProcess() =>
        new(string.IsNullOrEmpty(typeof(MediaToolResolver).Assembly.Location) ? AppContext.BaseDirectory : null);

    public (string Ffprobe, string Ffmpeg) Resolve()
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var (ffprobeName, ffmpegName) = MediaToolLocations.ToolNames(_windows);
        var directories = MediaToolLocations.CandidateDirectories(
            _getEnvironmentVariable(MediaToolLocations.FfmpegDirEnvironmentVariable),
            userHome,
            _packagedAppDirectory,
            _windows);
        foreach (var directory in directories)
        {
            var ffprobe = MediaToolLocations.Join(_windows, directory, ffprobeName);
            var ffmpeg = MediaToolLocations.Join(_windows, directory, ffmpegName);
            if (_files.FileExists(ffprobe) && _files.FileExists(ffmpeg))
            {
                return (ffprobe, ffmpeg);
            }
        }

        var pathEnvironment = _getEnvironmentVariable("PATH");
        var foundProbe = MediaToolLocations.Which("ffprobe", pathEnvironment, _windows, IsExecutableFile);
        var foundMpeg = MediaToolLocations.Which("ffmpeg", pathEnvironment, _windows, IsExecutableFile);
        if (foundProbe is null || foundMpeg is null)
        {
            throw new MediaToolException(MediaToolLocations.MissingToolsMessage);
        }

        return (foundProbe, foundMpeg);
    }

    /// <summary>
    /// #548: <c>WEIR_MKVTOOLNIX_DIR</c>, then (packaged builds only) the one next to the executable, then PATH —
    /// the same order as <see cref="Resolve"/>, and read fresh on every call for the same reason. Null when
    /// mkvmerge is nowhere to be found.
    /// </summary>
    public string? ResolveMkvmerge()
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var name = MediaToolLocations.MkvmergeToolName(_windows);
        var directories = MediaToolLocations.MkvtoolnixCandidateDirectories(
            _getEnvironmentVariable(MediaToolLocations.MkvtoolnixDirEnvironmentVariable),
            userHome,
            _packagedAppDirectory,
            _windows);
        foreach (var directory in directories)
        {
            var candidate = MediaToolLocations.Join(_windows, directory, name);
            if (_files.FileExists(candidate))
            {
                return candidate;
            }
        }

        return MediaToolLocations.Which("mkvmerge", _getEnvironmentVariable("PATH"), _windows, IsExecutableFile);
    }

    /// <summary>A PATH candidate counts when it exists, is not a directory, and (off Windows) is executable.</summary>
    private bool IsExecutableFile(string candidate)
    {
        if (!_files.FileExists(candidate))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            var mode = _files.GetUnixFileMode(candidate);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
