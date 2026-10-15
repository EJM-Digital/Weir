using System.ComponentModel;
using System.Diagnostics;
using Weir.Core.Media;
using Weir.Infrastructure.Processes;

namespace Weir.Infrastructure.Media;

/// <summary>Hardware acceleration detection: what <c>ffmpeg -hwaccels</c> reports.</summary>
public sealed partial class MediaTools
{
    /// <summary>
    /// The hardware acceleration methods ffmpeg was built with. Never throws for a missing, failing or slow ffmpeg;
    /// the report says why nothing was found.
    /// </summary>
    /// <remarks>
    /// <c>ffmpeg -hwaccels</c>'s output is decoded as UTF-8 with replacement (<see cref="ProbeOutput.CapturedText"/>),
    /// like every other tool output this class reads, rather than with the locale encoding, which can raise or
    /// mangle text on a locale that is not UTF-8 (#539 item 5). The method names it detects (<c>cuda</c>,
    /// <c>qsv</c>, …) are ASCII, so a mangled heading line is simply filtered out.
    /// <para>
    /// The probe runs at normal priority, not the below-normal priority of a media pass: it is a one-second question, and
    /// at below normal on a saturated machine it gets the processor only in short turns and outlasts any sensible limit.
    /// </para>
    /// </remarks>
    public Task<AccelerationReport> DetectAccelerationAsync(string ffmpegBin, CancellationToken cancellationToken = default) =>
        DetectAccelerationAsync(ffmpegBin, TimeSpan.FromSeconds(FfmpegCommands.HwaccelTimeoutSeconds), cancellationToken);

    /// <summary>
    /// <see cref="DetectAccelerationAsync(string, CancellationToken)"/> with the wait made explicit. Production
    /// always goes through that overload, which fixes it at <see cref="FfmpegCommands.HwaccelTimeoutSeconds"/> —
    /// right for a live settings check, too tight for a real build on a CI runner busy with other tests, which
    /// needs more room without borrowing the UI's own budget (see <c>RealFfmpegTests</c>).
    /// </summary>
    internal async Task<AccelerationReport> DetectAccelerationAsync(string ffmpegBin, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ffmpegBin);
        var argv = FfmpegCommands.BuildHwaccelsArgv(ffmpegBin);
        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(
                new ProcessRequest
                {
                    Argv = argv,
                    Timeout = timeout,
                    Stdin = ProcessInput.Inherit,
                    Stdout = ProcessOutput.Capture,
                    Stderr = ProcessOutput.Capture,
                    Priority = ProcessPriorityClass.Normal,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return HardwareAcceleration.ReportForRunError(error.Message);
        }

        if (result.TimedOut)
        {
            return HardwareAcceleration.ReportForTimeout();
        }

        return HardwareAcceleration.ReportFromHwaccels(result.ExitCode, ProbeOutput.CapturedText(result.Stdout));
    }
}
