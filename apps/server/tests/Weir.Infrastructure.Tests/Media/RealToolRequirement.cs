namespace Weir.Infrastructure.Tests.Media;

/// <summary>
/// Whether the real-tool tests may skip. Where the tools are optional (a workstation), a real-tool test skips with the
/// reason it names. Where <c>WEIR_REQUIRE_REAL_TOOLS=1</c> (CI), a missing tool is a failure instead, so a job that lost its
/// ffmpeg or mkvmerge cannot report green with the tests unrun.
/// </summary>
internal static class RealToolRequirement
{
    public const string RequireVariable = "WEIR_REQUIRE_REAL_TOOLS";

    public const string FfmpegMissing = "No working ffprobe and ffmpeg were found: set WEIR_FFMPEG_DIR or put them on PATH to run the real-ffmpeg tests.";

    public const string MkvmergeMissing = "No working mkvmerge was found: set WEIR_MKVTOOLNIX_DIR or put it on PATH to run the real-mkvmerge tests.";

    public const string FfmpegMissingForMkvmergeFixtures = "No working ffprobe and ffmpeg were found, and the mkvmerge tests generate their fixtures with them.";

    public static bool Required => IsRequired(Environment.GetEnvironmentVariable(RequireVariable));

    public static bool IsRequired(string? value) => value == "1";

    /// <summary>Why a real-ffmpeg test skips, or null when it runs (the tool is there, or is required and the test must fail instead).</summary>
    public static string? FfmpegSkipReason(bool required, bool ffmpegFound) =>
        required || ffmpegFound ? null : FfmpegMissing;

    /// <summary>Why a real-mkvmerge test skips, or null when it runs. Its fixtures come from ffmpeg, so a missing ffmpeg is named first.</summary>
    public static string? MkvmergeSkipReason(bool required, bool ffmpegFound, bool mkvmergeFound)
    {
        if (required)
        {
            return null;
        }

        if (!ffmpegFound)
        {
            return FfmpegMissingForMkvmergeFixtures;
        }

        return mkvmergeFound ? null : MkvmergeMissing;
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
            missing.Add(FfmpegMissing);
        }

        if (!mkvmergeFound)
        {
            missing.Add(MkvmergeMissing);
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"{RequireVariable}=1, so the real-tool tests may not be skipped. {string.Join(' ', missing)}");
        }
    }

    /// <summary>Fails the fixtures every real-tool test class shares when the tools are required and not all there.</summary>
    public static void EnsureAvailable()
    {
        try
        {
            EnsureAvailable(Required, RealFfmpeg.Tools is not null, RealMkvmerge.Tool is not null);
        }
        catch (InvalidOperationException missing) when (RealToolAvailability.Refused.Count > 0)
        {
            throw new InvalidOperationException($"{missing.Message} {string.Join(' ', RealToolAvailability.Refused)}", missing);
        }
    }
}
