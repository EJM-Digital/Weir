using System.Globalization;
using Weir.Core.Json;
using Weir.Core.Observability;
using Weir.Core.Rules;

namespace Weir.Core.Media;

/// <summary>
/// The sentences a person (or a media manager) reads when ffprobe, ffmpeg or mkvmerge fail. A tool's own text carries file
/// paths and memory addresses, so it stays in technical detail; this is the one mapping from a tool failure to plain words.
/// </summary>
public static class ToolFailureText
{
    /// <summary>A file the probe could not parse, or that has no streams at all.</summary>
    public const string UnreadableFile = "Weir couldn't read this file: it isn't a video Weir recognises, or it is damaged or incomplete.";

    /// <summary>What a pass says of a file it cannot read yet, while it waits to see whether the file is only still arriving.</summary>
    public const string NotReadableYet =
        "Weir can't read this file yet. It may still be arriving, so Weir will look again later.";

    /// <summary>What a pass says when it will not delete a file it could not read, whatever the workflow does with rejected files.</summary>
    public const string UnreadableKept =
        "Weir left the file where it is: it could not read it, so it cannot be sure the file is unwanted.";

    /// <summary>The sentence a pass records when it refuses a file it still could not read after looking again, before writing anything.</summary>
    public static string UnreadableFileRefusal(long looks, long minutes) =>
        $"{UnreadableFile} Weir looked at it {looks.ToString(CultureInfo.InvariantCulture)} times over about " +
        $"{minutes.ToString(CultureInfo.InvariantCulture)} minutes and it did not change. It was refused before any output was written.";

    /// <summary>A file that reads in part but not from start to finish.</summary>
    public const string IncompleteRead =
        "Weir could not read this media file from start to finish. It may still be downloading or may be incomplete, so Weir will wait.";

    public const string TookTooLong =
        "Weir's media tools took too long on this file, so Weir stopped them. Try the file again; if it keeps happening, the file may be damaged.";

    public const string Stalled =
        "Weir's media tools stopped making progress on this file, so Weir stopped them. Check that the drive or network share holding it is still working, then try the file again.";

    public const string NoSpace = "The drive Weir was writing to ran out of space. Free some space, then try the file again.";

    public const string NotAllowed =
        "Weir wasn't allowed to open or write a file it needed. Check that Weir can read the watched folder and write to its work and output folders.";

    public const string MissingOrEmpty = "Weir found no data in this file: it is missing or empty.";

    /// <summary>What a person reads when a file could not be put in its final place in the output folder.</summary>
    public const string OutputNotPlaced =
        "Weir could not put the file in the output folder, so nothing was published and no half-written copy was left behind. " +
        "Check that the output folder has room, that Weir can write to it, and that nothing else is using that name.";

    public const string OutputNotPublishable =
        "Weir wrote a cleaned copy, but its checks found a problem with it, so the copy was not published. The original is untouched.";

    public const string TooSlow =
        "Weir stopped writing this file because it would have taken more than 12 hours. The file may be damaged or mislabelled.";

    public const string WriterMissing = "Weir could not find the tool it needs to write this file. Check that Weir's video tools are installed.";

    /// <summary>What a person reads when the track details a probe returned cannot be made sense of.</summary>
    public const string UnusableTrackData =
        "Weir couldn't make sense of this file's track details, so it cannot plan anything for it. The file may be damaged.";

    public const string Generic =
        "Weir's media tools couldn't finish this file. Try it again; if it keeps happening, the file may be damaged.";

    /// <summary>
    /// The words for <paramref name="exception"/>: the sentence the failure carries for a person, else the one its type calls for,
    /// else its own message, which is then a sentence Weir wrote.
    /// </summary>
    public static string Plain(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            IPlainFailure { PlainMessage: { Length: > 0 } plain } => plain,
            MediaUnreadableException => UnreadableFile,
            MediaToolTimeoutException => TookTooLong,
            _ => exception.Message,
        };
    }

    /// <summary>
    /// The words for any failure met while running a media tool: <see cref="Plain"/> for one the tools raised, and
    /// <see cref="Generic"/> for anything else, whose own message may be a path or a system's text.
    /// </summary>
    public static string ForFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is MediaToolException or MediaToolTimeoutException or MkvmergeUnsupportedPlanException ? Plain(exception) : Generic;
    }

    /// <summary>
    /// The sentence for a failure whose cause the media tools name: an unreadable file, a timeout, or a tool failure with words of its
    /// own. Null for any other exception, which is not known to be a media failure.
    /// </summary>
    public static string? KnownReason(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is MediaToolException { PlainMessage: { Length: > 0 } } or MediaUnreadableException or MediaToolTimeoutException
            ? Plain(exception)
            : null;
    }

    /// <summary>The failure's own message for technical detail: what the tool said, with secrets redacted and capped at 1000 characters.</summary>
    public static string Technical(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return WireStrings.Slice(Diagnostics.SanitizeText("technical_detail", exception.Message), 1000);
    }

    /// <summary>
    /// What each line of a tool's text says after the file it names: ffprobe and ffmpeg write <c>path: message</c>, and a file called
    /// "The End of File.mkv" must not read as the tool saying so. A line with no <c>": "</c> is kept whole.
    /// </summary>
    public static string WithoutPaths(string toolText)
    {
        ArgumentNullException.ThrowIfNull(toolText);
        return string.Join(
            '\n',
            toolText.Split('\n').Select(line =>
            {
                var colon = line.LastIndexOf(": ", StringComparison.Ordinal);
                return colon < 0 ? line : line[(colon + 2)..];
            }));
    }

    /// <summary>The words for a tool's own failure text (stderr, a path, an address).</summary>
    public static string ForToolText(string toolText)
    {
        ArgumentNullException.ThrowIfNull(toolText);
        var lowered = WithoutPaths(RulesJson.Lower(toolText));
        if (ProbeOutput.IsUnreadableMedia(lowered))
        {
            return UnreadableFile;
        }

        if (lowered.Contains("timed out", StringComparison.Ordinal))
        {
            return TookTooLong;
        }

        if (lowered.Contains("no space left", StringComparison.Ordinal) || lowered.Contains("not enough space", StringComparison.Ordinal))
        {
            return NoSpace;
        }

        if (lowered.Contains("permission denied", StringComparison.Ordinal) || lowered.Contains("access is denied", StringComparison.Ordinal))
        {
            return NotAllowed;
        }

        return Generic;
    }
}
