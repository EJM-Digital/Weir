using Weir.Core.Json;

namespace Weir.Core.Media;

/// <summary>
/// Where Weir looks for ffprobe and ffmpeg, and the PATH search, as pure functions of the environment.
/// The filesystem checks are passed in.
/// </summary>
public static class MediaToolLocations
{
    /// <summary>glibc's default search path (<c>CS_PATH</c>), used when PATH is unset.</summary>
    public const string PosixDefaultPath = "/bin:/usr/bin";

    public const string FfmpegDirEnvironmentVariable = "WEIR_FFMPEG_DIR";

    /// <summary>#548: where a source install points Weir at MKVToolNix, beside <see cref="FfmpegDirEnvironmentVariable"/>.</summary>
    public const string MkvtoolnixDirEnvironmentVariable = "WEIR_MKVTOOLNIX_DIR";

    /// <summary>The bundled subdirectory MKVToolNix lives in, as <c>ffmpeg</c> is for ffprobe and ffmpeg.</summary>
    public const string MkvtoolnixBundleDirectory = "mkvtoolnix";

    public const string MissingToolsMessage =
        "Weir could not find the video tools it needs. Windows and Docker installs should include them; "
        + "source installs must provide ffprobe and ffmpeg on PATH or set WEIR_FFMPEG_DIR.";

    /// <summary>(ffprobe, ffmpeg) file names for the platform.</summary>
    public static (string Ffprobe, string Ffmpeg) ToolNames(bool windows) =>
        windows ? ("ffprobe.exe", "ffmpeg.exe") : ("ffprobe", "ffmpeg");

    /// <summary>#548: the mkvmerge file name for the platform.</summary>
    public static string MkvmergeToolName(bool windows) => windows ? "mkvmerge.exe" : "mkvmerge";

    /// <summary>
    /// #548: the directories checked for mkvmerge, in order, mirroring <see cref="CandidateDirectories"/>:
    /// <c>WEIR_MKVTOOLNIX_DIR</c> (expanded, not resolved), then, for a packaged app, the tools bundled beside
    /// the executable (<c>&lt;app&gt;/bin/mkvtoolnix</c> and <c>&lt;app&gt;/_internal/bin/mkvtoolnix</c>).
    /// Weir's data folder (<c>WEIR_HOME</c>) is never searched: on Windows it is writable by every local
    /// account by default, so a tool found there cannot be trusted over the one Weir ships.
    /// </summary>
    /// <param name="mkvtoolnixDirEnvironment">The raw <c>WEIR_MKVTOOLNIX_DIR</c> value, or null.</param>
    /// <param name="userHome">What <c>~</c> expands to.</param>
    /// <param name="packagedAppDirectory">The packaged executable's directory, or null when not packaged.</param>
    /// <param name="windows">Windows path rules.</param>
    public static IReadOnlyList<string> MkvtoolnixCandidateDirectories(
        string? mkvtoolnixDirEnvironment,
        string userHome,
        string? packagedAppDirectory,
        bool windows)
    {
        ArgumentNullException.ThrowIfNull(userHome);
        var candidates = new List<string>();
        var rawEnvDir = WireStrings.Strip(mkvtoolnixDirEnvironment ?? string.Empty);
        if (rawEnvDir.Length > 0)
        {
            candidates.Add(Normalize(ExpandUser(rawEnvDir, userHome, windows), windows));
        }

        if (packagedAppDirectory is not null)
        {
            candidates.Add(Join(windows, packagedAppDirectory, "bin", MkvtoolnixBundleDirectory));
            candidates.Add(Join(windows, packagedAppDirectory, "_internal", "bin", MkvtoolnixBundleDirectory));
        }

        return candidates;
    }

    /// <summary>
    /// The directories checked in order, lexically normalized (see <see cref="Normalize"/>): <c>WEIR_FFMPEG_DIR</c>
    /// (expanded, not resolved), then, for a packaged app, the tools bundled beside the executable
    /// (<c>&lt;app&gt;/bin/ffmpeg</c> and <c>&lt;app&gt;/_internal/bin/ffmpeg</c>). Weir's data folder
    /// (<c>WEIR_HOME</c>) is never searched: on Windows it is writable by every local account by default, so a
    /// tool found there cannot be trusted over the one Weir ships.
    /// </summary>
    /// <param name="ffmpegDirEnvironment">The raw <c>WEIR_FFMPEG_DIR</c> value, or null.</param>
    /// <param name="userHome">What <c>~</c> expands to.</param>
    /// <param name="packagedAppDirectory">The packaged executable's directory; null when not running packaged.</param>
    /// <param name="windows">Windows path rules.</param>
    public static IReadOnlyList<string> CandidateDirectories(
        string? ffmpegDirEnvironment,
        string userHome,
        string? packagedAppDirectory,
        bool windows)
    {
        ArgumentNullException.ThrowIfNull(userHome);
        var candidates = new List<string>();
        var rawEnvDir = WireStrings.Strip(ffmpegDirEnvironment ?? string.Empty);
        if (rawEnvDir.Length > 0)
        {
            candidates.Add(Normalize(ExpandUser(rawEnvDir, userHome, windows), windows));
        }

        if (packagedAppDirectory is not null)
        {
            candidates.Add(Join(windows, packagedAppDirectory, "bin", "ffmpeg"));
            candidates.Add(Join(windows, packagedAppDirectory, "_internal", "bin", "ffmpeg"));
        }

        return candidates;
    }

    /// <summary>Joins the parts with the platform separator, then normalizes (see <see cref="Normalize"/>).</summary>
    public static string Join(bool windows, string first, params string[] rest)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(rest);
        var separator = windows ? "\\" : "/";
        var combined = first;
        foreach (var part in rest)
        {
            combined = combined.Length == 0 ? part : combined + separator + part;
        }

        return Normalize(combined, windows);
    }

    /// <summary>
    /// Lexical normalization: separators unified, repeated separators and <c>.</c> segments dropped, no
    /// trailing separator. <c>..</c> is kept, since collapsing it without the filesystem can change the target.
    /// </summary>
    public static string Normalize(string path, bool windows)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            return ".";
        }

        string prefix;
        string rest;
        if (windows)
        {
            path = path.Replace('/', '\\');
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                // UNC: \\server\share\ is the anchor.
                var parts = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    prefix = @"\\" + parts[0] + "\\" + parts[1] + "\\";
                    rest = string.Join('\\', parts.Skip(2));
                }
                else
                {
                    prefix = @"\\";
                    rest = string.Join('\\', parts);
                }
            }
            else if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
            {
                var drive = path[..2];
                var afterDrive = path[2..];
                prefix = afterDrive.StartsWith('\\') ? drive + "\\" : drive;
                rest = afterDrive;
            }
            else
            {
                prefix = path.StartsWith('\\') ? "\\" : string.Empty;
                rest = path;
            }
        }
        else
        {
            prefix = path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("///", StringComparison.Ordinal)
                ? "//"
                : path.StartsWith('/') ? "/" : string.Empty;
            rest = path;
        }

        var sep = windows ? '\\' : '/';
        var segments = rest.Split(sep, StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".");
        var joined = string.Join(sep, segments);
        var result = prefix + joined;
        return result.Length == 0 ? "." : result;
    }

    /// <summary>Expands <c>~</c> and <c>~/...</c> to the user's home; other forms are left alone.</summary>
    public static string ExpandUser(string path, string userHome, bool windows)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(userHome);
        if (!path.StartsWith('~'))
        {
            return path;
        }

        if (path.Length == 1)
        {
            return userHome;
        }

        var next = path[1];
        return next == '/' || (windows && next == '\\') ? userHome.TrimEnd('/', '\\') + path[1..] : path;
    }

    /// <summary>
    /// The PATH search for a bare command name. On Windows this looks only for an <c>.exe</c> — PATHEXT is
    /// never consulted, and the current directory is never searched — so a <c>.bat</c>/<c>.cmd</c> file, or a
    /// program planted in the working directory, can never run in place of the real tool. Directories are
    /// deduplicated case-insensitively on Windows.
    /// </summary>
    /// <param name="command">A bare command name such as <c>ffprobe</c>.</param>
    /// <param name="pathEnvironment">PATH, or null when unset.</param>
    /// <param name="windows">Windows rules.</param>
    /// <param name="isExecutableFile">True when the candidate exists, is executable and is not a directory.</param>
    public static string? Which(string command, string? pathEnvironment, bool windows, Func<string, bool> isExecutableFile)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(isExecutableFile);
        var path = pathEnvironment ?? (windows ? "C:\\bin" : PosixDefaultPath);
        if (path.Length == 0)
        {
            return null;
        }

        var directories = path.Split(windows ? ';' : ':');
        var file = windows && !command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? command + ".exe" : command;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in directories)
        {
            // An empty entry means the current directory to a POSIX shell; Weir never looks there.
            if (directory.Length == 0)
            {
                continue;
            }

            var normalized = windows ? directory.Replace('/', '\\').ToLowerInvariant() : directory;
            if (!seen.Add(normalized))
            {
                continue;
            }

            var candidate = OsPathJoin(directory, file, windows);
            if (isExecutableFile(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Joins a directory and a relative file name, adding a separator only when one is missing.</summary>
    private static string OsPathJoin(string directory, string file, bool windows)
    {
        if (windows)
        {
            var last = directory[^1];
            var bareDrive = directory.Length == 2 && directory[1] == ':';
            return last is '\\' or '/' || bareDrive ? directory + file : directory + "\\" + file;
        }

        return directory.EndsWith('/') ? directory + file : directory + "/" + file;
    }
}
