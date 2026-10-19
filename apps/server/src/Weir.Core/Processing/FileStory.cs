using System.Globalization;
using Weir.Core.Json;
using Weir.Core.Processing.RemuxPass;
using Weir.Core.Rules;
using Weir.Core.Text;

namespace Weir.Core.Processing;

/// <summary>How a story step should read (not a severity).</summary>
public enum StoryTone
{
    Neutral,
    Good,
    Warn,
    Bad,
}

/// <summary>One plain-language step in what happened to a file.</summary>
public sealed record StoryStep(string Heading, string Sentence, StoryTone Tone = StoryTone.Neutral);

/// <summary>Where a finished file goes next, which decides how its last step is told.</summary>
public enum HandBackTarget
{
    /// <summary>The workflow is linked to a media manager, which imports the cleaned copy.</summary>
    MediaManager,

    /// <summary>The workflow has no media manager: the cleaned copy stays in its output folder.</summary>
    OutputFolder,
}

/// <summary>
/// Tell the story of what happened to a file, in plain language (#468).
/// Narrated at read time from the stored <c>file_logs.detail_json</c> payload; never raises on a
/// partial or malformed record.
/// </summary>
public static class FileStory
{
    private static readonly HashSet<string> EmptyTrackLines = new(StringComparer.Ordinal) { "", "—", "-", "none", "None" };

    /// <summary>The steps of one pass, in the order they happened.</summary>
    public static IReadOnlyList<StoryStep> NarratePass(WireObject? detail, string libraryName = "", HandBackTarget target = HandBackTarget.MediaManager)
    {
        if (detail is null)
        {
            return [];
        }

        var ok = !(detail.TryGetValue("ok", out var okValue) && okValue is WireBool { Value: false });

        var candidates = new List<StoryStep?>
        {
            PickedUp(detail, libraryName),
            LookedInside(detail),
            Planned(detail, ok),
            Choices(detail),
        };

        if (ok)
        {
            candidates.Add(Worked(detail));
            candidates.Add(Verified(detail));
            candidates.Add(Collision(detail));
            candidates.Add(HandedBack(detail, ok: true, target));
        }
        else
        {
            candidates.Add(Failed(detail));
        }

        return [.. candidates.Where(step => step is not null).Select(step => step!)];
    }

    private static string Text(WireValue? value) => value switch
    {
        null or WireNull => string.Empty,
        WireString s => string.Join(' ', s.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
        WireInteger i => i.Value.ToString(CultureInfo.InvariantCulture),
        WireNumber f => f.Value.ToString(CultureInfo.InvariantCulture),
        WireBool b => b.Value ? "True" : "False",
        _ => string.Empty,
    };

    private static List<string> ListOf(WireValue? value)
    {
        if (value is not WireArray list)
        {
            return [];
        }

        return [.. list.Items.Select(Text).Where(text => text.Length > 0)];
    }

    private static double? Number(WireValue? value) => value switch
    {
        WireInteger i => (double)i.Value,
        WireNumber f => double.IsNaN(f.Value) ? null : f.Value,
        WireBool b => b.Value ? 1.0 : 0.0,
        WireString s when double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static string Bytes(double value)
    {
        var size = Math.Abs(value);
        foreach (var unit in new[] { "B", "KB", "MB", "GB" })
        {
            if (size < 1024)
            {
                var shown = unit == "B" || size >= 100 ? size.ToString("F0", CultureInfo.InvariantCulture) : size.ToString("F1", CultureInfo.InvariantCulture);
                return $"{(value < 0 ? "-" : string.Empty)}{shown} {unit}";
            }

            size /= 1024;
        }

        var tbShown = size >= 100 ? size.ToString("F0", CultureInfo.InvariantCulture) : size.ToString("F1", CultureInfo.InvariantCulture);
        return $"{(value < 0 ? "-" : string.Empty)}{tbShown} TB";
    }

    private static string Duration(double seconds)
    {
        if (seconds < 1)
        {
            return "under a second";
        }

        if (seconds < 90)
        {
            var whole = (long)Math.Round(seconds, MidpointRounding.AwayFromZero);
            return $"{whole} second{(whole != 1 ? "s" : string.Empty)}";
        }

        var minutes = (long)Math.Round(seconds / 60, MidpointRounding.AwayFromZero);
        if (minutes < 90)
        {
            return $"{minutes} minutes";
        }

        var hours = seconds / 3600;
        return $"{hours.ToString("F1", CultureInfo.InvariantCulture)} hours";
    }

    private static string? TrackLine(WireValue? value)
    {
        var line = Text(value);
        return EmptyTrackLines.Contains(line) ? null : line;
    }

    private static StoryStep? PickedUp(WireObject detail, string libraryName)
    {
        var where = libraryName.Trim();
        var scope = Text(detail.TryGetValue("media_scope", out var s) ? s : null);
        var kind = scope switch { "tv" => "TV show", "movie" => "film", _ => string.Empty };
        if (where.Length == 0 && kind.Length == 0)
        {
            return null;
        }

        var sentence = "Weir took this file";
        if (kind.Length > 0)
        {
            sentence += $" as a {kind}";
        }

        if (where.Length > 0)
        {
            sentence += $" in the {where} workflow";
        }

        return new StoryStep("Picked up", $"{sentence}.");
    }

    private static StoryStep? LookedInside(WireObject detail)
    {
        var counts = detail.TryGetValue("stream_counts", out var raw) && raw is WireObject countsDict ? countsDict : null;
        var video = (long)(counts is not null && counts.TryGetValue("video", out var v) ? Number(v) ?? 0 : 0);
        var audio = (long)(counts is not null && counts.TryGetValue("audio", out var a) ? Number(a) ?? 0 : 0);
        var subs = (long)(counts is not null && counts.TryGetValue("subtitle", out var sub) ? Number(sub) ?? 0 : 0);
        var audioLine = TrackLine(detail.TryGetValue("audio_before", out var ab) ? ab : null);
        var subsLine = TrackLine(detail.TryGetValue("subs_before", out var sb) ? sb : null);

        if (counts is null && audioLine is null && subsLine is null)
        {
            return null;
        }

        var sentence = counts is not null
            ? $"It found {Plural.Of(video, "video track")}, {Plural.Of(audio, "audio track")} and {Plural.Of(subs, "subtitle track")}."
            : "It looked inside the file.";
        if (audioLine is not null)
        {
            sentence += $" Audio: {audioLine}.";
        }

        if (subsLine is not null)
        {
            sentence += $" Subtitles: {subsLine}.";
        }

        return new StoryStep("Looked inside", sentence);
    }

    private static StoryStep? Planned(WireObject detail, bool ok)
    {
        if (detail.TryGetValue("pass_through_unchanged", out var passThrough) && passThrough is WireBool { Value: true })
        {
            return new StoryStep(
                "Planned",
                "You asked for this file to pass through unchanged, so Weir planned to hand it on exactly as it arrived.");
        }

        var removedAudio = ListOf(detail.TryGetValue("removed_audio", out var ra) ? ra : null);
        var removedSubs = ListOf(detail.TryGetValue("removed_subtitles", out var rs) ? rs : null);
        var removedImages = ListOf(detail.TryGetValue("removed_images", out var ri) ? ri : null);
        var removedAttachments = ListOf(detail.TryGetValue("removed_attachments", out var rat) ? rat : null);
        var audioAfter = TrackLine(detail.TryGetValue("audio_after", out var aa) ? aa : null);
        var subsAfter = TrackLine(detail.TryGetValue("subs_after", out var sa) ? sa : null);
        var remuxRequired = detail.TryGetValue("remux_required", out var rr) ? rr : null;

        if (remuxRequired is WireBool { Value: false })
        {
            return new StoryStep("Planned", "Everything already matched your settings, so there was nothing to change.", StoryTone.Good);
        }

        var changes = new List<string>();
        if (removedAudio.Count > 0)
        {
            changes.Add($"remove {Plural.Of(removedAudio.Count, "audio track")}");
        }

        if (removedSubs.Count > 0)
        {
            changes.Add($"remove {Plural.Of(removedSubs.Count, "subtitle track")}");
        }

        if (removedImages.Count > 0)
        {
            changes.Add($"remove {Plural.Of(removedImages.Count, "embedded image")}");
        }

        if (removedAttachments.Count > 0)
        {
            changes.Add($"remove {Plural.Of(removedAttachments.Count, "attachment")}");
        }

        if (detail.TryGetValue("metadata_removed", out var metadataRemoved) && metadataRemoved.IsTruthy)
        {
            changes.Add("strip the file's metadata");
        }

        if (changes.Count == 0 && audioAfter is null && subsAfter is null)
        {
            return null;
        }

        var sentence = changes.Count > 0 ? $"Weir planned to {string.Join(", ", changes)}." : "Weir planned the output tracks.";
        if (audioAfter is not null)
        {
            sentence += $" Audio kept: {audioAfter}.";
        }

        if (subsAfter is not null)
        {
            sentence += $" Subtitles kept: {subsAfter}.";
        }

        if (ok && remuxRequired is WireBool { Value: true } && VideoWasStreamCopied(detail))
        {
            sentence += " The video is copied, not re-encoded, so picture quality is unchanged.";
        }

        return new StoryStep("Planned", sentence);
    }

    /// <summary>True only when the stored ffmpeg command demonstrably copied the video stream.</summary>
    private static bool VideoWasStreamCopied(WireObject detail)
    {
        if (!detail.TryGetValue("ffmpeg_argv", out var raw) || raw is not WireArray list)
        {
            return false;
        }

        var args = list.Items.Select(Text).ToList();
        var copiesEverything = false;
        for (var index = 0; index < args.Count - 1; index++)
        {
            var arg = args[index];
            var following = args[index + 1];
            if ((arg is "-c:v" or "-codec:v" or "-vcodec") && following != "copy")
            {
                return false;
            }

            if ((arg is "-c" or "-codec" or "-c:v" or "-codec:v" or "-vcodec") && following == "copy")
            {
                copiesEverything = true;
            }
        }

        return copiesEverything;
    }

    private static StoryStep? Choices(WireObject detail)
    {
        var notes = ListOf(detail.TryGetValue("audio_selection_notes", out var raw) ? raw : null);
        return notes.Count == 0
            ? null
            : new StoryStep("Why these tracks", string.Join(' ', notes.Take(4).Select(note => note.TrimEnd('.') + ".")));
    }

    private static StoryStep? Worked(WireObject detail)
    {
        var elapsed = Number(detail.TryGetValue("elapsed_seconds", out var e) ? e : null);
        var source = Number(detail.TryGetValue("source_size_bytes", out var src) ? src : null);
        var output = Number(detail.TryGetValue("output_size_bytes", out var outp) ? outp : null);
        if (elapsed is null && (source is null || output is null))
        {
            return null;
        }

        var bits = new List<string>();
        if (elapsed is { } el && el > 0)
        {
            bits.Add($"It took {Duration(el)}");
        }

        var good = false;
        if (source is { } src2 && output is { } out2 && src2 > 0)
        {
            var saved = src2 - out2;
            if (saved > 0)
            {
                bits.Add($"{Bytes(src2)} became {Bytes(out2)}, saving {Bytes(saved)}");
                good = true;
            }
            else if (saved < 0)
            {
                bits.Add($"{Bytes(src2)} became {Bytes(out2)}");
            }
            else
            {
                bits.Add($"the size stayed at {Bytes(src2)}");
            }
        }

        if (bits.Count == 0)
        {
            return null;
        }

        var sentence = string.Join("; ", bits);
        sentence = char.ToUpperInvariant(sentence[0]) + sentence[1..] + ".";
        return new StoryStep("Worked", sentence, good ? StoryTone.Good : StoryTone.Neutral);
    }

    private static StoryStep? Verified(WireObject detail)
    {
        var check = Text(detail.TryGetValue("output_completeness_check", out var c) ? c : null).ToLowerInvariant();
        var note = Text(detail.TryGetValue("output_completeness_note", out var n) ? n : null);
        return check switch
        {
            "passed" => new StoryStep("Checked", "Weir checked the finished file was complete before handing it on.", StoryTone.Good),
            "failed" => new StoryStep("Checked", note.Length > 0 ? note : "The finished file did not pass Weir's completeness check.", StoryTone.Bad),
            _ => null,
        };
    }

    private static StoryStep? Collision(WireObject detail)
    {
        var reason = Text(detail.TryGetValue("output_collision_reason", out var r) ? r : null);
        if (reason.Length == 0)
        {
            return null;
        }

        var action = Text(detail.TryGetValue("output_collision_action", out var a) ? a : null).ToLowerInvariant();
        return new StoryStep("An output already existed", reason, action == "skip" ? StoryTone.Warn : StoryTone.Neutral);
    }

    private static StoryStep? HandedBack(WireObject detail, bool ok, HandBackTarget target)
    {
        if (!ok)
        {
            return null;
        }

        var destination = Text(detail.TryGetValue("output_file", out var d) ? d : null);
        if (destination.Length == 0)
        {
            return null;
        }

        return target == HandBackTarget.OutputFolder
            ? new StoryStep("Cleaned copy", $"The cleaned copy is in the output folder, at {destination}.", StoryTone.Good)
            : new StoryStep("Handed back", $"The result was written to {destination} for your media manager to import.", StoryTone.Good);
    }

    private static StoryStep Failed(WireObject detail)
    {
        var reason = Text(detail.TryGetValue("reason", out var r) ? r : null);
        if (reason.Length == 0)
        {
            reason = Text(detail.TryGetValue("preflight_reason", out var pr) ? pr : null);
        }

        if (RemuxPassVisibility.Truthy(detail.Get(RejectionResultKeys.WithoutManager)))
        {
            return Rejected(detail, reason);
        }

        return new StoryStep("Could not finish", reason.Length > 0 ? reason : "Weir could not process this file, and the record does not say why.", StoryTone.Bad);
    }

    /// <summary>A rejection by the rules is a decision, not a failure: it says why, then what became of the file.</summary>
    private static StoryStep Rejected(WireObject detail, string reason)
    {
        var lead = AudioRejectionExplanation.Lead.TrimEnd();
        var why = reason.StartsWith(lead, StringComparison.Ordinal) ? reason[lead.Length..].TrimStart() : reason;
        why = why.Length > 0 ? char.ToUpperInvariant(why[0]) + why[1..] : "Weir rejected this file.";
        why = why.EndsWith('.') ? why : $"{why}.";
        var next = Text(detail.Get("rejected_cleanup_detail"));
        return new StoryStep("Rejected", next.Length > 0 ? $"{why} {next}" : why, StoryTone.Warn);
    }
}
