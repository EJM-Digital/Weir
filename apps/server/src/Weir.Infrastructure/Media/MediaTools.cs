using Microsoft.Extensions.Logging;
using Weir.Core.Media;
using Weir.Infrastructure.IO;
using Weir.Infrastructure.Processes;

namespace Weir.Infrastructure.Media;

/// <summary>
/// ffprobe and ffmpeg execution: probing, output validation, the full-read integrity check, remuxing with progress, and hardware detection. Decisions live in
/// <see cref="Weir.Core.Media"/>; this class runs the tools and hands their output over.
/// </summary>
/// <remarks>
/// Split by concern across several partial-class files, one type per concern: probing
/// (<c>MediaTools.Probing.cs</c>), file state (<c>MediaTools.FileInspection.cs</c>), the media integrity check
/// (<c>MediaTools.Integrity.cs</c>), running ffmpeg itself (<c>MediaTools.Ffmpeg.cs</c>), remuxing to a temp file
/// and validating the result (<c>MediaTools.Remux.cs</c>), hardware acceleration detection
/// (<c>MediaTools.Acceleration.cs</c>), tool version reporting (<c>MediaTools.Versions.cs</c>) and mkvmerge
/// (<c>MediaTools.Mkvmerge.cs</c>). This file holds only the fields, constructors and the one logger shared by
/// both external tools' debug summaries.
/// </remarks>
public sealed partial class MediaTools
{
    private readonly IProcessRunner _runner;
    private readonly IMediaToolResolver _resolver;
    private readonly ILogger<MediaTools> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly bool _windows = OperatingSystem.IsWindows();
    private readonly Func<string, MediaFileState> _inspectFile;

    public MediaTools(IProcessRunner runner, IMediaToolResolver resolver, ILogger<MediaTools> logger, TimeProvider timeProvider)
        : this(runner, resolver, logger, timeProvider, InspectFile)
    {
    }

    /// <summary>For tests: file state comes from <paramref name="inspectFile"/> instead of the filesystem.</summary>
    internal MediaTools(IProcessRunner runner, IMediaToolResolver resolver, ILogger<MediaTools> logger, TimeProvider timeProvider, Func<string, MediaFileState> inspectFile)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(inspectFile);
        _inspectFile = inspectFile;
        _runner = runner;
        _resolver = resolver;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>The file system the work file is created through; settable so a test can make the operating system refuse or race it.</summary>
    internal IFileSystem Files { get; init; } = PhysicalFileSystem.Instance;

    /// <summary>How long a tool that reports progress may say nothing before it is stopped; settable so tests need not wait it out.</summary>
    internal TimeSpan SilenceLimit { get; init; } = TimeSpan.FromSeconds(ToolTimeLimits.SilenceSeconds);

    /// <summary>How long ffmpeg is given to exit after <c>progress=end</c>; settable so tests need not wait it out.</summary>
    internal TimeSpan FinishedExitGrace { get; init; } = TimeSpan.FromSeconds(ToolTimeLimits.FinishedExitSeconds);

    /// <summary>
    /// The request fields that make a run of ffmpeg with <c>-progress pipe:1</c> stop only when it stops getting anywhere: the idle
    /// clock restarts when the output time, output size or frame count grows, not on every block ffmpeg prints, and a process that has
    /// printed its final block but does not exit is stopped after <see cref="FinishedExitGrace"/>.
    /// </summary>
    private ProcessRequest WithProgressWatch(ProcessRequest request, FfmpegProgressAdvance advance) =>
        request with
        {
            IdleTimeout = SilenceLimit,
            MarksProgress = advance.Feed,
            IsFinalLine = FfmpegProgressAdvance.IsEnd,
            ExitAfterFinalLine = FinishedExitGrace,
        };

    /// <summary>The stdout lines of a run that reads them as they arrive, kept to be read as one text when it ends.</summary>
    private sealed class StdoutLines
    {
        private readonly System.Text.StringBuilder _text = new();

        public string Text => _text.ToString();

        public void Add(string line) => _text.Append(line).Append('\n');
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "ffmpeg finished but did not exit; Weir stopped it and is checking the file it wrote as usual.")]
    private partial void LogFfmpegFinishedWithoutExiting();

    /// <summary>
    /// <c>logger.debug</c>'s one-line summary of an argv about to run, shared by the ffmpeg and mkvmerge remux
    /// writes (<see cref="FfmpegCommands.DebugSummary"/> and <see cref="MkvmergeCommands.DebugSummary"/>).
    /// </summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "{Summary}")]
    private partial void LogFfmpegDebug(string summary);
}
