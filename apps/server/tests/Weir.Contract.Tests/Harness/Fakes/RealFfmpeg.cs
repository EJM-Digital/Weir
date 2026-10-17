namespace Weir.Contract.Tests.Harness.Fakes;

/// <summary>
/// The real ffprobe and ffmpeg, for the few scenarios that need genuine media tools on tiny generated files. They are found on
/// PATH, or in <c>WEIR_CONTRACT_REAL_FFMPEG_DIR</c> when that names a folder; a scenario marked <see cref="RealFfmpegFactAttribute"/>
/// or <see cref="RealFfmpegTheoryAttribute"/> is skipped when they are missing. mkvmerge is found the same way (on PATH, or in
/// <c>WEIR_CONTRACT_REAL_MKVTOOLNIX_DIR</c>) and is optional, as it is for the server: a scenario marked
/// <see cref="RealMkvmergeFactAttribute"/> needs it, and a server started with <see cref="Env"/> uses it for Matroska when it is there.
/// Where <c>WEIR_REQUIRE_REAL_TOOLS=1</c> (CI) none of that skips: a scenario that finds a tool missing fails, naming the tool, so a
/// leg that lost its install cannot report green with the scenarios unrun.
/// </summary>
public static class RealFfmpeg
{
    public const string RequireVariable = "WEIR_REQUIRE_REAL_TOOLS";

    public const string DirectoryVariable = "WEIR_CONTRACT_REAL_FFMPEG_DIR";

    public const string MkvtoolnixDirectoryVariable = "WEIR_CONTRACT_REAL_MKVTOOLNIX_DIR";

    public const string MissingReason = "ffmpeg and ffprobe are not on PATH (or WEIR_CONTRACT_REAL_FFMPEG_DIR)";

    public const string MissingMkvmergeReason = "mkvmerge is not on PATH (or WEIR_CONTRACT_REAL_MKVTOOLNIX_DIR)";

    public const string WindowsOnlyReason = "Only Windows refuses to replace a file another program has open; elsewhere the swap goes ahead.";

    private static readonly Lazy<string?> Located = new(Locate);
    private static readonly Lazy<string?> LocatedMkvmerge = new(LocateMkvmerge);

    /// <summary>Whether a missing tool fails the scenarios that need it instead of skipping them.</summary>
    public static bool Required => IsRequired(Environment.GetEnvironmentVariable(RequireVariable));

    public static bool IsRequired(string? value) => value == "1";

    /// <summary>Why a real-ffmpeg scenario skips, or null when it runs (the tools are there, or are required and the scenario must fail instead).</summary>
    public static string? SkipReason(bool required, bool ffmpegFound) => required || ffmpegFound ? null : MissingReason;

    /// <summary>Why a real-mkvmerge scenario skips, or null when it runs. ffmpeg is named first, because the scenario's media comes from it.</summary>
    public static string? MkvmergeSkipReason(bool required, bool ffmpegFound, bool mkvmergeFound)
    {
        if (required)
        {
            return null;
        }

        return !ffmpegFound ? MissingReason : mkvmergeFound ? null : MissingMkvmergeReason;
    }

    /// <summary>Throws, naming each missing tool as its skip would have, when the tools are required and one is missing.</summary>
    public static void EnsureAvailable(bool required, bool ffmpegFound, bool mkvmergeFound)
    {
        if (!required)
        {
            return;
        }

        var missing = new List<string>();
        if (!ffmpegFound)
        {
            missing.Add(MissingReason);
        }

        if (!mkvmergeFound)
        {
            missing.Add(MissingMkvmergeReason);
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"{RequireVariable}=1, so the real-tool scenarios may not be skipped. {string.Join(" and ", missing)}.");
        }
    }

    /// <summary>A folder holding both tools, or null.</summary>
    public static string? Folder => Located.Value;

    public static bool IsAvailable => Folder is not null;

    /// <summary>A folder holding mkvmerge, or null.</summary>
    public static string? MkvtoolnixFolder => LocatedMkvmerge.Value;

    public static bool IsMkvmergeAvailable => IsAvailable && MkvtoolnixFolder is not null;

    /// <summary>What the server needs to find the tools: pass it to <see cref="WeirServer.StartNewAsync"/>.</summary>
    public static IReadOnlyDictionary<string, string> Env
    {
        get
        {
            EnsureAvailable(Required, IsAvailable, MkvtoolnixFolder is not null);
            var environment = new Dictionary<string, string> { ["WEIR_FFMPEG_DIR"] = Folder ?? throw new InvalidOperationException(MissingReason) };
            if (MkvtoolnixFolder is { } mkvtoolnix)
            {
                environment["WEIR_MKVTOOLNIX_DIR"] = mkvtoolnix;
            }

            return environment;
        }
    }

    private static string? Locate()
    {
        var explicitFolder = Environment.GetEnvironmentVariable(DirectoryVariable)?.Trim();
        if (!string.IsNullOrEmpty(explicitFolder))
        {
            return Directory.Exists(explicitFolder) ? explicitFolder : null;
        }

        var ffmpeg = OnPath("ffmpeg");
        var ffprobe = OnPath("ffprobe");
        if (ffmpeg is null || ffprobe is null)
        {
            return null;
        }

        var folder = Path.GetDirectoryName(Resolved(ffmpeg));
        return folder == Path.GetDirectoryName(Resolved(ffprobe)) ? folder : null;
    }

    private static string? LocateMkvmerge()
    {
        var explicitFolder = Environment.GetEnvironmentVariable(MkvtoolnixDirectoryVariable)?.Trim();
        if (!string.IsNullOrEmpty(explicitFolder))
        {
            return Directory.Exists(explicitFolder) ? explicitFolder : null;
        }

        return OnPath("mkvmerge") is { } mkvmerge ? Path.GetDirectoryName(Resolved(mkvmerge)) : null;
    }

    private static string Resolved(string path) =>
        File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);

    private static string? OnPath(string program)
    {
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return folders
            .SelectMany(folder => extensions.Select(extension => Path.Combine(folder, program + extension)))
            .FirstOrDefault(File.Exists);
    }
}

/// <summary>A test that uses the real ffmpeg and ffprobe; it is skipped when they are not available, unless they are required.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RealFfmpegFactAttribute : FactAttribute
{
    public RealFfmpegFactAttribute()
    {
        Skip = RealFfmpeg.SkipReason(RealFfmpeg.Required, RealFfmpeg.IsAvailable);
    }
}

/// <summary>A parametrised test that uses the real ffmpeg and ffprobe; it is skipped when they are not available, unless they are required.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RealFfmpegTheoryAttribute : TheoryAttribute
{
    public RealFfmpegTheoryAttribute()
    {
        Skip = RealFfmpeg.SkipReason(RealFfmpeg.Required, RealFfmpeg.IsAvailable);
    }
}

/// <summary>A test that needs the real mkvmerge as well as ffmpeg and ffprobe; it is skipped when any of them is missing, unless they are required.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RealMkvmergeFactAttribute : FactAttribute
{
    public RealMkvmergeFactAttribute()
    {
        Skip = RealFfmpeg.MkvmergeSkipReason(RealFfmpeg.Required, RealFfmpeg.IsAvailable, RealFfmpeg.MkvtoolnixFolder is not null);
    }
}

/// <summary>A test that needs the real ffmpeg and ffprobe on Windows, whose file locks the other platforms have no counterpart for; it is skipped elsewhere, and where the tools are required it fails on Windows rather than skipping.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RealFfmpegWindowsFactAttribute : FactAttribute
{
    public RealFfmpegWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = RealFfmpeg.WindowsOnlyReason;
        }
        else
        {
            Skip = RealFfmpeg.SkipReason(RealFfmpeg.Required, RealFfmpeg.IsAvailable);
        }
    }
}
