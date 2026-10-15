using System.ComponentModel;
using System.Diagnostics;
using Weir.Core.Media;
using Weir.Infrastructure.Processes;

namespace Weir.Infrastructure.Media;

/// <summary>#548: what each external media tool on this machine says it is, for the media-tools endpoint.</summary>
public sealed partial class MediaTools
{
    /// <summary>
    /// #548: what each external media tool on this machine says it is, for <c>GET /api/v1/system/media-tools</c>.
    /// Never throws: every way of failing to get an answer — the tool is not installed, will not start, exits
    /// non-zero, or hangs — becomes a string in the report, because the point of the endpoint is to tell an
    /// operator what is wrong with an install, and an endpoint that 500s when the install is broken tells them
    /// nothing. mkvmerge in particular is optional (<see cref="IMediaToolResolver.ResolveMkvmerge"/> returns null
    /// rather than throwing), so its absence reports <see cref="MediaToolVersions.NotInstalled"/>, not an error.
    /// </summary>
    /// <remarks>
    /// Nothing is cached. This runs only when an operator asks, at most two short-lived processes per request,
    /// and caching would hide exactly the change an operator is most likely to be checking for — that the
    /// mkvmerge they have just installed, or the <c>WEIR_MKVTOOLNIX_DIR</c> they have just set, is now found.
    /// The resolver reads the environment fresh on every call for the same reason.
    /// </remarks>
    public async Task<MediaToolVersionReport> DescribeVersionsAsync(CancellationToken cancellationToken = default)
    {
        string? ffmpeg;
        try
        {
            (_, ffmpeg) = _resolver.Resolve();
        }
        catch (MediaToolException)
        {
            ffmpeg = null;
        }

        var ffmpegVersion = ffmpeg is null
            ? MediaToolVersions.NotInstalled
            : await DescribeToolVersionAsync(FfmpegCommands.BuildVersionArgv(ffmpeg), cancellationToken).ConfigureAwait(false);

        var mkvmerge = _resolver.ResolveMkvmerge();
        var mkvmergeVersion = mkvmerge is null
            ? MediaToolVersions.NotInstalled
            : await DescribeToolVersionAsync(MkvmergeCommands.BuildVersionArgv(mkvmerge), cancellationToken).ConfigureAwait(false);

        return new MediaToolVersionReport(ffmpegVersion, mkvmergeVersion);
    }

    /// <summary>
    /// Runs one <c>--version</c> argv and reduces whatever happened to a single string. The timeout is
    /// <see cref="FfmpegCommands.HwaccelTimeoutSeconds"/>, the same one
    /// <see cref="DetectAccelerationAsync(string, CancellationToken)"/> uses for the other "ask the tool about
    /// itself" call: printing a version does no I/O and no decoding, so a tool that has not answered in that
    /// time is not going to. It runs at normal priority, like that call, so a busy machine does not starve it into
    /// a false "unknown".
    /// </summary>
    private async Task<string> DescribeToolVersionAsync(IReadOnlyList<string> argv, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(
                new ProcessRequest
                {
                    Argv = argv,
                    Timeout = TimeSpan.FromSeconds(FfmpegCommands.HwaccelTimeoutSeconds),
                    Stdin = ProcessInput.Inherit,
                    Stdout = ProcessOutput.Capture,
                    Stderr = ProcessOutput.Capture,
                    Priority = ProcessPriorityClass.Normal,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The resolver found the file a moment ago, so this is a file that exists but will not execute:
            // the wrong architecture, a partial download, or a permission the install never granted.
            return MediaToolVersions.Unknown;
        }

        if (result.TimedOut)
        {
            return MediaToolVersions.Unknown;
        }

        // mkvmerge prints its banner on stdout; so does ffmpeg with -hide_banner. Older ffmpeg builds put the
        // whole version block on stderr, so stderr is the fallback rather than being discarded.
        var stdout = MediaToolVersions.FromBanner(result.ExitCode, ProbeOutput.CapturedText(result.Stdout));
        return stdout == MediaToolVersions.Unknown
            ? MediaToolVersions.FromBanner(result.ExitCode, ProbeOutput.CapturedText(result.Stderr))
            : stdout;
    }
}
