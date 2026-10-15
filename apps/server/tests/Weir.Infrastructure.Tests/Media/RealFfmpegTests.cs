using System.ComponentModel;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using Weir.Core.Media;
using Weir.Core.Rules;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.Processes;

namespace Weir.Infrastructure.Tests.Media;

/// <summary>A fact that runs only where a working ffprobe and ffmpeg can be found (WEIR_FFMPEG_DIR or PATH), unless <see cref="RealToolRequirement"/> makes them required.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresFfmpegFactAttribute : FactAttribute
{
    public RequiresFfmpegFactAttribute()
    {
        Skip = RealToolRequirement.FfmpegSkipReason(RealToolRequirement.Required, RealFfmpeg.Tools is not null);
    }
}

/// <summary>A fact that runs only on Windows where a working ffmpeg can be found: named-pipe blocking behaviour.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresFfmpegOnWindowsFactAttribute : FactAttribute
{
    public RequiresFfmpegOnWindowsFactAttribute()
    {
        Skip = OperatingSystem.IsWindows()
            ? RealToolRequirement.FfmpegSkipReason(RealToolRequirement.Required, RealFfmpeg.Tools is not null)
            : "Windows named pipes are the mechanism this test blocks ffmpeg on.";
    }
}

/// <summary>#548: a fact that runs only where a working mkvmerge can be found (WEIR_MKVTOOLNIX_DIR or PATH), unless <see cref="RealToolRequirement"/> makes it required.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresMkvmergeFactAttribute : FactAttribute
{
    public RequiresMkvmergeFactAttribute()
    {
        Skip = RealToolRequirement.MkvmergeSkipReason(RealToolRequirement.Required, RealFfmpeg.Tools is not null, RealMkvmerge.Tool is not null);
    }
}

internal static class RealMkvmerge
{
    public static readonly string? Tool = Find();

    private static string? Find()
    {
        var resolved = new MediaToolResolver().ResolveMkvmerge();
        return resolved is not null && RealToolAvailability.Runs(MkvmergeCommands.BuildVersionArgv(resolved)) ? resolved : null;
    }
}

internal static class RealFfmpeg
{
    public static readonly (string Ffprobe, string Ffmpeg)? Tools = Find();

    private static (string, string)? Find()
    {
        (string Ffprobe, string Ffmpeg) resolved;
        try
        {
            resolved = new MediaToolResolver().Resolve();
        }
        catch (MediaToolException)
        {
            return null;
        }

        return RealToolAvailability.Runs(FfmpegCommands.BuildVersionArgv(resolved.Ffprobe))
            && RealToolAvailability.Runs(FfmpegCommands.BuildVersionArgv(resolved.Ffmpeg))
            ? resolved
            : null;
    }
}

/// <summary>
/// Whether a path <see cref="MediaToolResolver"/> resolved is a build that actually runs, shared by every real-tool
/// detection in this assembly. A path can resolve (the file exists under <c>WEIR_FFMPEG_DIR</c>/<c>PATH</c>) without
/// the tool being usable — a stale directory left from an old install, or a Windows execution-alias stub — and that
/// showed up as "fixture generation failed" deep inside a real test instead of a skip with a clear reason.
/// </summary>
internal static class RealToolAvailability
{
    /// <summary>
    /// A working tool answers at once; the wait is long because a starved machine can take many seconds to start one, and
    /// a check that gives up then reads as "not installed" and skips every real-tool test without a word.
    /// </summary>
    private static readonly TimeSpan VersionCheckTimeout = TimeSpan.FromSeconds(60);

    public static bool Runs(IReadOnlyList<string> versionArgv)
    {
        try
        {
            var result = new ProcessRunner()
                .RunAsync(new ProcessRequest { Argv = versionArgv, Timeout = VersionCheckTimeout })
                .GetAwaiter()
                .GetResult();
            return !result.TimedOut && result.ExitCode == 0;
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>
/// The ffmpeg layer against real ffprobe and ffmpeg on tiny files generated with <c>-f lavfi</c> at test time:
/// probing, a remux that drops an audio track, validation, truncation and unreadable input. These pin the current
/// behaviour with the real tools, including where that is not what one would want.
/// </summary>
public sealed class RealFfmpegTests(RealFfmpegFixtures fixtures) : IDisposable, IClassFixture<RealFfmpegFixtures>
{
    private readonly string _root = Directory.CreateTempSubdirectory("weir-real-ffmpeg-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static MediaTools Tools(IProcessRunner? runner = null) =>
        new(runner ?? new ProcessRunner(), new StaticResolver(), new ListLogger<MediaTools>(), TimeProvider.System);

    private static List<JsonElement> Streams(JsonElement probe, string codecType) =>
        probe.GetProperty("streams").EnumerateArray().Where(s => s.GetProperty("codec_type").GetString() == codecType).ToList();

    private static string? StreamTitle(JsonElement stream) =>
        stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("title", out var title) ? title.GetString() : null;

    [RequiresFfmpegFact]
    public async Task Probe_reads_streams_and_duration()
    {
        var fixture = await fixtures.StandardAsync();

        var probe = await Tools().FfprobeJsonAsync(fixture);

        Assert.Single(Streams(probe, "video"));
        var audio = Streams(probe, "audio");
        Assert.Equal(["eng", "fre"], audio.Select(s => s.GetProperty("tags").GetProperty("language").GetString()));
        var duration = ProbeOutput.DurationSeconds(probe);
        Assert.NotNull(duration);
        Assert.InRange(duration.Value, 2.9, 3.2);
    }

    [RequiresFfmpegFact]
    public async Task A_remux_drops_an_audio_track_reports_progress_and_validates()
    {
        var fixture = await fixtures.StandardAsync();
        var tools = Tools();
        var probe = await tools.FfprobeJsonAsync(fixture);
        var config = RemuxRules.DefaultConfig() with { PrimaryAudioLang = "eng", SecondaryAudioLang = string.Empty, TertiaryAudioLang = string.Empty };
        var split = RemuxRules.SplitStreams(new ProbeResult(probe));
        var plan = RemuxRules.PlanRemux(split.Video, split.Audio, split.Subtitles, config);
        Assert.NotNull(plan);
        Assert.Single(plan.Audio);
        var updates = new List<FfmpegProgressUpdate>();
        var workDir = Path.Combine(_root, "work");

        var staged = await tools.RemuxToTempFileAsync(fixture, workDir, plan, probe, [], updates.Add, ProbeOutput.DurationSeconds(probe));
        var output = staged.Path;

        Assert.StartsWith(Path.Combine(Path.GetFullPath(workDir), "fixture.processing."), output, StringComparison.Ordinal);
        var outputProbe = await tools.FfprobeJsonAsync(output);
        var audio = Assert.Single(Streams(outputProbe, "audio"));
        Assert.Equal("eng", audio.GetProperty("tags").GetProperty("language").GetString());
        Assert.Single(Streams(outputProbe, "video"));
        Assert.NotEmpty(updates);
        Assert.Equal("end", updates[^1].Progress);
        Assert.Equal(100.0, updates[^1].Percent);
    }

    [RequiresFfmpegFact]
    public async Task A_correct_remux_that_keeps_the_planned_subtitle_passes_staged_validation()
    {
        var fixture = await fixtures.WithSubtitleAsync();
        var tools = Tools();
        var probe = await tools.FfprobeJsonAsync(fixture);
        var config = RemuxRules.DefaultConfig() with
        {
            PrimaryAudioLang = "eng",
            SecondaryAudioLang = string.Empty,
            TertiaryAudioLang = string.Empty,
            SubtitleMode = RemuxRuleValues.SubtitleModeKeepSelected,
            SubtitleLangs = ["eng"],
        };
        var split = RemuxRules.SplitStreams(new ProbeResult(probe));
        var plan = RemuxRules.PlanRemux(split.Video, split.Audio, split.Subtitles, config);
        Assert.NotNull(plan);
        Assert.Single(plan.Subtitles);
        var sourceWarnings = await tools.ProbeWarningLinesAsync(fixture);
        var workDir = Path.Combine(_root, "work");

        // RemuxToTempFileAsync's internal call to ValidateStagedOutputAsync (#500) not throwing is the assertion:
        // a correctly-remuxed output that keeps the planned subtitle, disposition and language passes.
        var staged = await tools.RemuxToTempFileAsync(fixture, workDir, plan, probe, sourceWarnings, durationSeconds: ProbeOutput.DurationSeconds(probe));
        var output = staged.Path;

        var outputProbe = await tools.FfprobeJsonAsync(output);
        Assert.Single(Streams(outputProbe, "subtitle"));
    }

    [RequiresFfmpegFact]
    public async Task An_output_that_drops_a_subtitle_the_plan_kept_fails_staged_validation()
    {
        var fixture = await fixtures.WithSubtitleAsync();
        var tools = Tools();
        var probe = await tools.FfprobeJsonAsync(fixture);
        var config = RemuxRules.DefaultConfig() with
        {
            PrimaryAudioLang = "eng",
            SecondaryAudioLang = string.Empty,
            TertiaryAudioLang = string.Empty,
            SubtitleMode = RemuxRuleValues.SubtitleModeKeepSelected,
            SubtitleLangs = ["eng"],
        };
        var split = RemuxRules.SplitStreams(new ProbeResult(probe));
        var plan = RemuxRules.PlanRemux(split.Video, split.Audio, split.Subtitles, config);
        Assert.NotNull(plan);
        Assert.Single(plan.Subtitles);
        var sourceWarnings = await tools.ProbeWarningLinesAsync(fixture);

        // A deliberately wrong remux: video and audio only, dropping the subtitle the plan says to keep.
        var wrongOutput = Path.Combine(_root, "wrong-output.mkv");
        await RealFfmpegFixtures.RunAsync(
            "Making the output that drops the subtitle",
            RealFfmpegFixtures.Ffmpeg("-i", fixture, "-map", "0:0", "-map", "0:1", "-c", "copy", wrongOutput));

        var error = await Assert.ThrowsAsync<MediaToolException>(
            () => tools.ValidateStagedOutputAsync(wrongOutput, fixture, probe, plan, sourceWarnings));

        Assert.Contains("subtitle", error.Message, StringComparison.Ordinal);
    }

    [RequiresFfmpegFact]
    public async Task A_remux_standardizes_names_clears_video_titles_and_removes_chapters()
    {
        // #498: real ffmpeg and ffprobe, not the golden fixtures, which do not cover this option.
        var fixture = await fixtures.WithChaptersAsync();
        var tools = Tools();
        var probe = await tools.FfprobeJsonAsync(fixture);
        var probeResult = new ProbeResult(probe);
        Assert.Equal(2, probeResult.Chapters.Count);

        var config = RemuxRules.DefaultConfig() with
        {
            PrimaryAudioLang = "eng",
            SecondaryAudioLang = string.Empty,
            TertiaryAudioLang = string.Empty,
            Metadata = new MetadataRules { StandardizeTrackNames = true, ClearVideoTrackNames = true, RemoveChapters = true },
        };
        var split = RemuxRules.SplitStreams(probeResult);
        var plan = RemuxRules.PlanRemux(split.Video, split.Audio, split.Subtitles, config);
        Assert.NotNull(plan);
        Assert.True(RemuxRules.IsRemuxRequired(plan, split.Audio, split.Subtitles, split.Video, chaptersPresent: true));
        var sourceWarnings = await tools.ProbeWarningLinesAsync(fixture);
        var workDir = Path.Combine(_root, "work");

        var staged = await tools.RemuxToTempFileAsync(fixture, workDir, plan, probe, sourceWarnings, durationSeconds: ProbeOutput.DurationSeconds(probe));
        var output = staged.Path;

        var outputProbeJson = await tools.FfprobeJsonAsync(output);
        var audio = Assert.Single(Streams(outputProbeJson, "audio"));
        Assert.Equal("English 1.0 AAC", StreamTitle(audio));
        var video = Assert.Single(Streams(outputProbeJson, "video"));
        Assert.True(string.IsNullOrEmpty(StreamTitle(video)));
        Assert.Empty(new ProbeResult(outputProbeJson).Chapters);
    }

    [RequiresFfmpegFact]
    public async Task A_remux_keeps_an_attachment_with_its_mimetype_and_passes_validation()
    {
        // #547 item 1: FfmpegCommands.BuildRemuxArgv never mapped codec_type=attachment streams, so a font
        // attached for an ASS/SSA subtitle was silently dropped on every remux even with RemoveAttachments off.
        var fixture = await fixtures.WithAttachmentAsync();
        var tools = Tools();
        var probe = await tools.FfprobeJsonAsync(fixture);
        var sourceAttachment = Assert.Single(Streams(probe, "attachment"));
        Assert.Equal("application/x-font-ttf", sourceAttachment.GetProperty("tags").GetProperty("mimetype").GetString());
        Assert.Equal("font.ttf", sourceAttachment.GetProperty("tags").GetProperty("filename").GetString());

        var config = RemuxRules.DefaultConfig() with
        {
            PrimaryAudioLang = "eng",
            SecondaryAudioLang = string.Empty,
            TertiaryAudioLang = string.Empty,
            SubtitleMode = RemuxRuleValues.SubtitleModeKeepSelected,
            SubtitleLangs = ["eng"],
        };
        var split = RemuxRules.SplitStreams(new ProbeResult(probe));
        var plan = RemuxRules.PlanRemux(split.Video, split.Audio, split.Subtitles, config);
        Assert.NotNull(plan);
        Assert.Single(plan.Subtitles);
        var workDir = Path.Combine(_root, "work-attachment");

        // RemuxToTempFileAsync also runs ValidateRemuxOutputAsync on the result, proving the output validator
        // (audio-stream count and duration) is not confused by the new attachment stream in the output.
        var sourceWarnings = await tools.ProbeWarningLinesAsync(fixture);
        var staged = await tools.RemuxToTempFileAsync(fixture, workDir, plan, probe, sourceWarnings, durationSeconds: ProbeOutput.DurationSeconds(probe));
        var output = staged.Path;

        var outputProbe = await tools.FfprobeJsonAsync(output);
        var outputAttachment = Assert.Single(Streams(outputProbe, "attachment"));
        Assert.Equal("application/x-font-ttf", outputAttachment.GetProperty("tags").GetProperty("mimetype").GetString());
        Assert.Equal("font.ttf", outputAttachment.GetProperty("tags").GetProperty("filename").GetString());
        Assert.Single(Streams(outputProbe, "subtitle"));
    }

    [RequiresFfmpegFact]
    public async Task A_remux_to_mp4_skips_attachments_the_container_cannot_carry()
    {
        // #547 item 1: the mov,mp4,m4a,3gp,3g2,mj2 muxer family refuses an attachment output stream outright, so
        // mapping "0:t?" for it fails the whole remux; BuildRemuxArgv skips the map for these extensions instead.
        var fixture = await fixtures.WithAttachmentAsync();
        var tools = Tools();
        var probe = await tools.FfprobeJsonAsync(fixture);
        var config = RemuxRules.DefaultConfig() with { PrimaryAudioLang = "eng", SecondaryAudioLang = string.Empty, TertiaryAudioLang = string.Empty };
        var split = RemuxRules.SplitStreams(new ProbeResult(probe));
        var plan = RemuxRules.PlanRemux(split.Video, split.Audio, split.Subtitles, config);
        Assert.NotNull(plan);
        var ffmpeg = RealFfmpeg.Tools!.Value.Ffmpeg;
        var mp4Dst = Path.Combine(_root, "attachment-out.mp4");
        var argv = FfmpegCommands.BuildRemuxArgv(ffmpeg, fixture, mp4Dst, plan);
        Assert.DoesNotContain("0:t?", argv);

        await RealFfmpegFixtures.RunAsync("The mp4 remux", argv);

        var outputProbe = await tools.FfprobeJsonAsync(mp4Dst);
        Assert.Empty(Streams(outputProbe, "attachment"));
    }

    [RequiresFfmpegFact]
    public async Task A_remux_keeps_the_comment_disposition_while_changing_default()
    {
        // #547 item 2: "-disposition:a:N default|0" overwrites the whole disposition, clearing "comment"
        // (and similarly "descriptions", "hearing_impaired", "dub", "original", ...) on a kept track. The
        // additive "+default"/"-default" syntax only ever touches default/forced.
        var fixture = await fixtures.WithCommentaryTrackAsync();

        var tools = Tools();
        var probe = await tools.FfprobeJsonAsync(fixture);
        var sourceAudio = Assert.Single(Streams(probe, "audio"));
        Assert.Equal(1, sourceAudio.GetProperty("disposition").GetProperty("comment").GetInt32());
        Assert.Equal(0, sourceAudio.GetProperty("disposition").GetProperty("default").GetInt32());

        // Issue #495 (landed after this test's own base) now detects "commentary" straight from the disposition
        // flag this fixture sets, not only from a track's name — correctly, but beside this test's point, which is
        // the additive disposition edit below, not commentary removal. Keep the track in the plan by disabling that
        // rule, exactly as a library that wants a lone commentary track kept would configure it.
        var config = RemuxRules.DefaultConfig() with { PrimaryAudioLang = "eng", SecondaryAudioLang = string.Empty, TertiaryAudioLang = string.Empty, RemoveCommentary = false };
        var split = RemuxRules.SplitStreams(new ProbeResult(probe));
        var plan = RemuxRules.PlanRemux(split.Video, split.Audio, split.Subtitles, config);
        Assert.NotNull(plan);
        var kept = Assert.Single(plan.Audio);
        Assert.True(kept.Default);
        var workDir = Path.Combine(_root, "work-commentary");

        var sourceWarnings = await tools.ProbeWarningLinesAsync(fixture);
        var staged = await tools.RemuxToTempFileAsync(fixture, workDir, plan, probe, sourceWarnings);
        var output = staged.Path;

        var outputProbe = await tools.FfprobeJsonAsync(output);
        var outputAudio = Assert.Single(Streams(outputProbe, "audio"));
        var disposition = outputAudio.GetProperty("disposition");
        Assert.Equal(1, disposition.GetProperty("default").GetInt32());
        Assert.Equal(1, disposition.GetProperty("comment").GetInt32());
    }

    [RequiresFfmpegFact]
    public async Task A_remux_drops_stale_statistics_tags_and_the_output_DURATION_matches_the_output()
    {
        // #547 item 3: DURATION/NUMBER_OF_FRAMES/NUMBER_OF_BYTES/BPS/_STATISTICS_*/ENCODER describe how the
        // *elementary stream* was produced, not this remux, and a plain "-c copy" carries them forward unchanged.
        var fixture = await fixtures.WithStaleStatisticsTagsAsync();

        var tools = Tools();
        var probe = await tools.FfprobeJsonAsync(fixture);
        var sourceAudio = Assert.Single(Streams(probe, "audio"));
        Assert.True(sourceAudio.GetProperty("tags").TryGetProperty("NUMBER_OF_FRAMES", out _), "fixture setup: source should carry the stale tag");

        var config = RemuxRules.DefaultConfig() with { PrimaryAudioLang = "eng", SecondaryAudioLang = string.Empty, TertiaryAudioLang = string.Empty };
        var split = RemuxRules.SplitStreams(new ProbeResult(probe));
        var plan = RemuxRules.PlanRemux(split.Video, split.Audio, split.Subtitles, config);
        Assert.NotNull(plan);
        var workDir = Path.Combine(_root, "work-stale-tags");

        var sourceWarnings = await tools.ProbeWarningLinesAsync(fixture);
        var staged = await tools.RemuxToTempFileAsync(fixture, workDir, plan, probe, sourceWarnings);
        var output = staged.Path;

        var outputProbe = await tools.FfprobeJsonAsync(output);
        var outputAudio = Assert.Single(Streams(outputProbe, "audio"));
        var outputVideo = Assert.Single(Streams(outputProbe, "video"));
        foreach (var stream in new[] { outputAudio, outputVideo })
        {
            Assert.True(stream.TryGetProperty("tags", out var tags));
            foreach (var staleKey in new[] { "NUMBER_OF_FRAMES", "NUMBER_OF_BYTES", "BPS", "_STATISTICS_WRITING_APP", "_STATISTICS_WRITING_DATE_UTC", "_STATISTICS_TAGS", "ENCODER" })
            {
                Assert.False(tags.TryGetProperty(staleKey, out _), $"{staleKey} should have been cleared");
            }
        }

        var outputDurationSeconds = ProbeOutput.DurationSeconds(outputProbe);
        Assert.NotNull(outputDurationSeconds);
        var audioDurationTag = outputAudio.GetProperty("tags").GetProperty("DURATION").GetString();
        Assert.NotNull(audioDurationTag);
        var parsedTagSeconds = ParseFfmpegTimeTag(audioDurationTag!);
        Assert.InRange(parsedTagSeconds, outputDurationSeconds!.Value - 0.5, outputDurationSeconds.Value + 0.5);
    }

    /// <summary>Parses a Matroska "DURATION"-style tag ("HH:MM:SS.fffffffff") into seconds, without relying on
    /// <see cref="TimeSpan.Parse(string)"/>'s 7-digit fraction limit against ffmpeg's 9-digit nanosecond tags.</summary>
    private static double ParseFfmpegTimeTag(string text)
    {
        var parts = text.Split(':');
        Assert.Equal(3, parts.Length);
        return (double.Parse(parts[0], CultureInfo.InvariantCulture) * 3600)
            + (double.Parse(parts[1], CultureInfo.InvariantCulture) * 60)
            + double.Parse(parts[2], CultureInfo.InvariantCulture);
    }

    [RequiresFfmpegFact]
    public async Task Complete_media_passes_both_validations()
    {
        var fixture = await fixtures.StandardAsync();
        var tools = Tools();

        Assert.Null(await Record.ExceptionAsync(() => tools.ValidateRemuxOutputAsync(fixture, expectedAudio: 2, expectedDurationSeconds: 3.0)));
        Assert.Null(await Record.ExceptionAsync(() => tools.ValidateMediaIntegrityAsync(fixture)));
    }

    [RequiresFfmpegFact]
    public async Task Output_shorter_than_expected_is_rejected_as_incomplete()
    {
        var fixture = await fixtures.ShortAsync();

        var error = await Assert.ThrowsAsync<MediaCompletenessException>(() => Tools().ValidateRemuxOutputAsync(fixture, expectedAudio: 2, expectedDurationSeconds: 30.0));

        Assert.Contains("of 30.0s expected", error.Message, StringComparison.Ordinal);
    }

    [RequiresFfmpegFact]
    public async Task A_truncated_download_fails_the_integrity_read()
    {
        // Moov first, so the header survives and only the media data is cut off.
        var fixture = await fixtures.Mp4Async();
        var bytes = await File.ReadAllBytesAsync(fixture);
        var truncated = Path.Combine(_root, "truncated.mp4");
        await File.WriteAllBytesAsync(truncated, bytes[..(bytes.Length / 2)]);

        var error = await Assert.ThrowsAsync<MediaCompletenessException>(() => Tools().ValidateMediaIntegrityAsync(truncated));

        Assert.Contains("could not read this media file from start to finish", error.Message, StringComparison.Ordinal);
    }

    [RequiresFfmpegFact]
    public async Task A_truncated_matroska_file_still_passes_the_header_duration_check()
    {
        // Pinned, not approved: Matroska keeps its duration in the header, so a file cut in half still reports the
        // full length and passes the staged-output check. This is why #539 item 3 checks the *integrity* read
        // (the next test) rather than this one: the header cannot be trusted, only a full demux can.
        var fixture = await fixtures.StandardAsync();
        var bytes = await File.ReadAllBytesAsync(fixture);
        var truncated = Path.Combine(_root, "truncated.mkv");
        await File.WriteAllBytesAsync(truncated, bytes[..(bytes.Length / 2)]);

        Assert.Null(await Record.ExceptionAsync(() => Tools().ValidateRemuxOutputAsync(truncated, expectedAudio: 2, expectedDurationSeconds: 3.0)));
    }

    [RequiresFfmpegFact]
    public async Task A_truncated_matroska_file_fails_the_fixed_integrity_read()
    {
        // #539 item 3: ffmpeg exits 0 from the full demux of a file cut in half, only warning "File ended
        // prematurely", so the exit code alone would call this file complete. ValidateMediaIntegrityAsync treats
        // that warning as failure (Weir.Core.Media.ProbeOutput.IntegrityIncompleteMarkers).
        var fixture = await fixtures.StandardAsync();
        var bytes = await File.ReadAllBytesAsync(fixture);
        var truncated = Path.Combine(_root, "truncated.mkv");
        await File.WriteAllBytesAsync(truncated, bytes[..(bytes.Length / 2)]);

        var error = await Assert.ThrowsAsync<MediaCompletenessException>(() => Tools().ValidateMediaIntegrityAsync(truncated, expectedDurationSeconds: 3.0));

        Assert.Contains("could not read this media file from start to finish", error.Message, StringComparison.Ordinal);
    }

    [RequiresFfmpegFact]
    public async Task Garbage_input_is_classified_unreadable_because_ffprobe_runs_with_v_error()
    {
        // #539 item 1: with "-v quiet" ffprobe never prints the words the unreadable-media markers look for, and
        // this would come back as a plain RuntimeError instead of MediaUnreadableError; "-v error"
        // (Weir.Core.Media.FfmpegCommands.BuildFfprobeArgv) puts them on stderr.
        var garbage = Path.Combine(_root, "garbage.mkv");
        await File.WriteAllBytesAsync(garbage, Enumerable.Range(1, 4096).Select(i => (byte)(i * 37 % 256)).ToArray());

        var error = await Assert.ThrowsAsync<MediaUnreadableException>(() => Tools().FfprobeJsonAsync(garbage));

        Assert.Contains("Invalid data found when processing input", error.Message, StringComparison.Ordinal);
    }

    [RequiresFfmpegFact]
    public async Task A_zero_filled_mkv_is_classified_unreadable()
    {
        // #539: the exact repro from issue #494 (a 10 GB all-zero .mkv on the Deluno rig, reported completed with
        // a copy of itself as output). One megabyte is enough for ffprobe to hit the header immediately.
        var zeroFilled = Path.Combine(_root, "zero-filled.mkv");
        await File.WriteAllBytesAsync(zeroFilled, new byte[1024 * 1024]);

        var error = await Assert.ThrowsAsync<MediaUnreadableException>(() => Tools().FfprobeJsonAsync(zeroFilled));

        Assert.Contains("EBML header parsing failed", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Wider than <see cref="FfmpegCommands.HwaccelTimeoutSeconds"/> which is sized for a live settings
    /// check, not a CI runner sharing the box with every other real-ffmpeg test in this class. Widening it here
    /// keeps a busy runner from making a real, working build look undetected.
    /// </summary>
    private static readonly TimeSpan HwaccelDetectionTimeoutForTests = TimeSpan.FromSeconds(60);

    [RequiresFfmpegFact]
    public async Task Hardware_detection_reads_the_real_build()
    {
        var report = await Tools().DetectAccelerationAsync(RealFfmpeg.Tools!.Value.Ffmpeg, HwaccelDetectionTimeoutForTests, CancellationToken.None);

        Assert.True(report.Detected, report.Detail);
        Assert.Equal(report.AvailableMethods.Order(StringComparer.Ordinal), report.AvailableMethods);
        Assert.All(report.AvailableMethods, method => Assert.Matches("^[a-z0-9_]+$", method));
    }

    [RequiresFfmpegFact]
    public async Task A_progress_run_past_its_time_limit_is_stopped()
    {
        var output = Path.Combine(_root, "slow.mkv");
        string[] argv =
        [
            RealFfmpeg.Tools!.Value.Ffmpeg, "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-re", "-f", "lavfi", "-i", "testsrc=duration=60:size=160x120:rate=10", "-c:v", "mpeg4",
            output,
        ];
        using var started = new StartedProcesses();

        var error = await Assert.ThrowsAsync<MediaToolException>(() => Tools(started).RunFfmpegAsync(argv, timeoutSeconds: 1, progressCallback: _ => { }, durationSeconds: 60));

        Assert.Equal("ffmpeg timed out", error.Message);
        Assert.Equal(1, started.Count);
        await started.AllHaveEndedAsync();
    }

    /// <summary>
    /// #539 item 4: a progress loop that only checks its timeout as a line arrives would hang forever on a
    /// process stuck reading its input - one that never gets to write a progress line at all. ProcessRunner's
    /// timeout is a wall-clock timer instead (see ProcessRunnerTests for the same guarantee without a real
    /// ffmpeg), so this is enforced even though nothing is ever read. A Windows named pipe with no writer
    /// makes ffmpeg block inside avformat_open_input, before it can emit anything.
    /// </summary>
    [RequiresFfmpegOnWindowsFact]
    public async Task A_progress_run_that_never_writes_a_line_is_still_stopped_by_the_timer()
    {
        var pipeName = "weir-hang-" + Guid.NewGuid().ToString("N");
        using var pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.Out);
        string[] argv =
        [
            RealFfmpeg.Tools!.Value.Ffmpeg, "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-i", @"\\.\pipe\" + pipeName, "-c", "copy", Path.Combine(_root, "hang.mkv"),
        ];
        var updates = new List<FfmpegProgressUpdate>();
        using var started = new StartedProcesses();

        var error = await Assert.ThrowsAsync<MediaToolException>(() => Tools(started).RunFfmpegAsync(argv, timeoutSeconds: 2, progressCallback: updates.Add));

        Assert.Equal("ffmpeg timed out", error.Message);
        Assert.Empty(updates);
        Assert.Equal(1, started.Count);
        await started.AllHaveEndedAsync();
    }

    private sealed class StaticResolver : IMediaToolResolver
    {
        public (string Ffprobe, string Ffmpeg) Resolve() => RealFfmpeg.Tools!.Value;

        public string? ResolveMkvmerge() => RealMkvmerge.Tool;
    }
}
