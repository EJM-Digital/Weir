using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Weir.Core.Media;
using Weir.Core.Rules;
using Weir.Infrastructure.Processes;

namespace Weir.Infrastructure.Media;

/// <summary>Writing a remux to a temp file (the workflow's writer choice, with an ffmpeg fallback) and #500's staged-output validation.</summary>
public sealed partial class MediaTools
{
    /// <summary>
    /// The writer for a file that will be written as <paramref name="destination"/>, under a workflow's
    /// "Writes files with" choice. Best means mkvmerge where it can take the file, and ffmpeg otherwise.
    /// </summary>
    public IRemuxWriter WriterFor(string destination, string? choice) =>
        new RemuxWriterSelector(new FfmpegRemuxWriter(this), new MkvmergeRemuxWriter(this, _resolver)).Select(destination, choice);

    /// <summary>
    /// Writes the remux into <paramref name="workDir"/> and validates it. The temp file is
    /// deleted on any failure; the caller owns moving or deleting it on success.
    /// </summary>
    /// <param name="src">The source file to remux.</param>
    /// <param name="workDir">Where the temp output is written.</param>
    /// <param name="plan">Which streams to keep and how to tag them.</param>
    /// <param name="sourceProbe">
    /// The source's own ffprobe JSON (already read by the caller before planning), for #500's staged-output
    /// validation: the container family to compare against and the kept streams' own durations.
    /// </param>
    /// <param name="sourceWarnings">
    /// The source's ffprobe <c>-v warning</c> lines (<see cref="RemuxOutputValidation.WarningLines"/>), read once by
    /// the caller, so #500's new-warnings check has a baseline to compare the output against.
    /// </param>
    /// <param name="progressCallback">Reported to as ffmpeg runs, when given.</param>
    /// <param name="durationSeconds">The expected output duration, for progress percentage only (validation derives its own expected duration from the kept streams; see <see cref="ValidateStagedOutputAsync"/>).</param>
    /// <param name="ffmpegInputFlags">
    /// Options ffmpeg reads before <c>-i</c> (its strictness). <see cref="FfmpegCommands.BuildRemuxArgv"/> is the
    /// one argv builder: its result is both what <see cref="LogFfmpegDebug"/> shows and what
    /// <see cref="RunFfmpegAsync"/> executes, so the logged flags cannot differ from the ones that run (#539 item 2).
    /// </param>
    /// <param name="writer">
    /// Which tool writes the output. Null means ffmpeg. Whatever writes it, the
    /// staged output is validated here by <see cref="ValidateStagedOutputAsync"/> in exactly the same way. When
    /// this is not ffmpeg and its output fails to be written or to validate, the file is written again with
    /// ffmpeg and validated again, so the preferred writer can only match or beat what ffmpeg alone would have
    /// produced.
    /// </param>
    /// <param name="keepOnFailure">
    /// Setup › Performance › Cleanup "Keep a failed file's half-written copy for a day": a copy that fails is left in the work folder to look at, not
    /// deleted. A cancelled write is removed either way.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<StagedRemux> RemuxToTempFileAsync(
        string src,
        string workDir,
        RemuxPlan plan,
        JsonElement sourceProbe,
        IReadOnlyList<string> sourceWarnings,
        Action<FfmpegProgressUpdate>? progressCallback = null,
        double? durationSeconds = null,
        IReadOnlyList<string>? ffmpegInputFlags = null,
        IRemuxWriter? writer = null,
        bool keepOnFailure = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(workDir);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sourceWarnings);
        Directory.CreateDirectory(workDir);
        var suffix = MediaPathNames.Suffix(src, _windows);
        var tmpPath = CreateTempFile(workDir, prefix: MediaPathNames.Stem(src, _windows) + ".processing.", suffix: suffix.Length > 0 ? suffix : ".mkv");
        try
        {
            var request = new RemuxWriteRequest(src, tmpPath, plan, sourceProbe, progressCallback, durationSeconds, ffmpegInputFlags);
            var chosen = writer ?? new FfmpegRemuxWriter(this);
            var usedWriter = chosen;
            try
            {
                await chosen.WriteAsync(request, cancellationToken).ConfigureAwait(false);
                // Whichever tool wrote it, #500's validation is the same and is run here rather than in
                // the writer, so no writer can grade its own work.
                await ValidateStagedOutputAsync(tmpPath, src, sourceProbe, plan, sourceWarnings, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (ShouldRewriteWithFfmpeg(error, chosen))
            {
                // A file mkvmerge declined, or wrote in a shape the validation above rejected, is written again
                // by ffmpeg and validated again. If this second attempt fails too, it throws and the outer catch
                // cleans up, exactly as for a plain ffmpeg write. The temp file is overwritten in place.
                LogWriterFellBack(error, chosen.Name, ToolFailureText.ForFailure(error));
                usedWriter = new FfmpegRemuxWriter(this);
                await usedWriter.WriteAsync(request, cancellationToken).ConfigureAwait(false);
                await ValidateStagedOutputAsync(tmpPath, src, sourceProbe, plan, sourceWarnings, cancellationToken).ConfigureAwait(false);
            }

            LogWriterUsed(usedWriter.Name, tmpPath);
            return new StagedRemux(tmpPath, usedWriter);
        }
        catch (Exception failure)
        {
            // "Keep a failed file's half-written copy for a day" (Setup › Performance › Cleanup): a copy that failed stays in the work folder for a person
            // to look at, until the leftover-work-file sweep finds it a day later. A cancelled write is never kept.
            if (keepOnFailure && failure is not OperationCanceledException && File.Exists(tmpPath))
            {
                LogFailedWorkFileKept(tmpPath);
                throw;
            }

            try
            {
                if (File.Exists(tmpPath))
                {
                    File.Delete(tmpPath);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                LogTempRemoveFailed(error, tmpPath);
            }

            throw;
        }
    }

    /// <summary>
    /// #500: validates a staged remux output against the whole plan instead of just an audio count and a duration
    /// floor — container family, per-position track type, counts per type, disposition and language per kept
    /// track, new ffprobe warnings, and cleared metadata (<see cref="RemuxOutputValidation.ValidateAgainstPlan"/>).
    /// This is the staged-output check the remux pass calls; <see cref="ValidateRemuxOutputAsync"/> is kept
    /// only for the golden-file tests of the simpler audio-count and duration check.
    /// </summary>
    /// <param name="outputPath">The staged output to validate.</param>
    /// <param name="sourcePath">
    /// The source file, used only when none of the kept streams' own probed duration is usable and a direct
    /// stream-copy measurement is needed (<see cref="MeasureKeptStreamsDurationAsync"/>).
    /// </param>
    /// <param name="sourceProbe">The source's own ffprobe JSON, read once by the caller before planning.</param>
    /// <param name="plan">The plan the output is checked against.</param>
    /// <param name="sourceWarnings">The source's ffprobe <c>-v warning</c> lines, read once by the caller.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task ValidateStagedOutputAsync(
        string outputPath,
        string sourcePath,
        JsonElement sourceProbe,
        RemuxPlan plan,
        IReadOnlyList<string> sourceWarnings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sourceWarnings);

        var expectedDuration = RemuxOutputValidation.ExpectedDurationFromKeptStreams(sourceProbe, plan)
            ?? await MeasureKeptStreamsDurationAsync(sourcePath, plan, cancellationToken).ConfigureAwait(false);
        if (expectedDuration is null)
        {
            throw new MediaCompletenessException(
                "Validation failed: Weir could not establish how long the kept streams should run — none of them reported a " +
                "duration and measuring the source directly did not produce one — so the staged output was not published.");
        }

        var output = await ProbeWithWarningsAsync(outputPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        RemuxOutputValidation.ValidateAgainstPlan(
            output.Probe,
            plan,
            RemuxOutputValidation.FormatName(sourceProbe),
            expectedDuration.Value,
            sourceWarnings,
            output.Warnings);
    }

    /// <summary>
    /// #500: when no kept source stream reports its own duration, demux exactly the streams the plan keeps
    /// (<see cref="FfmpegCommands.BuildKeptStreamsDemuxArgv"/>) and read the last timestamp reached, the same way
    /// <see cref="ValidateMediaIntegrityAsync"/> reads its progress. Null when the run timed out or reported no
    /// timestamp at all.
    /// </summary>
    public async Task<double?> MeasureKeptStreamsDurationAsync(string sourcePath, RemuxPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(plan);
        var (_, ffmpeg) = _resolver.Resolve();
        var argv = FfmpegCommands.WithProgress(FfmpegCommands.BuildKeptStreamsDemuxArgv(ffmpeg, sourcePath, plan));
        var progress = new StdoutLines();
        var result = await _runner.RunAsync(
            WithProgressWatch(
                new ProcessRequest
                {
                    Argv = argv,
                    Timeout = TimeSpan.FromSeconds(ToolTimeLimits.OverallSeconds(SizeOfFile(sourcePath))),
                    Stdin = ProcessInput.Null,
                    Stdout = ProcessOutput.Discard,
                    Stderr = ProcessOutput.Discard,
                    OnStdoutLine = progress.Add,
                },
                new FfmpegProgressAdvance()),
            cancellationToken).ConfigureAwait(false);
        if (result.Timeout == ProcessTimeoutKind.NotExitedAfterFinish)
        {
            LogFfmpegFinishedWithoutExiting();
        }

        return result.Timeout is ProcessTimeoutKind.Overall or ProcessTimeoutKind.Idle ? null : ProbeOutput.LastProgressOutTimeSeconds(progress.Text);
    }

    /// <summary>A new file named prefix + 8 random characters + suffix, created exclusively so no other writer shares it.</summary>
    private string CreateTempFile(string directory, string prefix, string suffix)
    {
        const string Characters = "abcdefghijklmnopqrstuvwxyz0123456789_";
        for (var attempt = 0; ; attempt++)
        {
            var name = prefix + RandomNumberGenerator.GetString(Characters, 8) + suffix;
            var path = Path.GetFullPath(Path.Combine(directory, name));
            try
            {
                using (Files.OpenFile(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                }

                return path;
            }
            catch (IOException) when (attempt < 100 && Files.FileExists(path))
            {
                // Name taken; draw again.
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove temp file {Path}")]
    private partial void LogTempRemoveFailed(Exception error, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kept the failed work file {Path} to look at; the leftover-work-file sweep removes it once it is a day old.")]
    private partial void LogFailedWorkFileKept(string path);

    /// <summary>
    /// Whether a failed write by <paramref name="chosen"/> should be attempted again with ffmpeg.
    /// <para>
    /// Only for a writer that is not already ffmpeg (there is nothing to fall back to), and never for
    /// cancellation: a cancelled job must stay cancelled rather than quietly start a second, longer write.
    /// Everything else is worth retrying: whether mkvmerge declined the plan, failed to run, or produced
    /// something <see cref="ValidateStagedOutputAsync"/> rejected, ffmpeg writing it is the outcome the user
    /// would have had without mkvmerge.
    /// </para>
    /// </summary>
    private static bool ShouldRewriteWithFfmpeg(Exception error, IRemuxWriter chosen) =>
        chosen is not FfmpegRemuxWriter && error is not OperationCanceledException;

    [LoggerMessage(Level = LogLevel.Information, Message = "{Writer} could not write this file, so ffmpeg is writing it instead: {Reason}")]
    private partial void LogWriterFellBack(Exception error, string writer, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Writer} wrote {Path}")]
    private partial void LogWriterUsed(string writer, string path);
}
