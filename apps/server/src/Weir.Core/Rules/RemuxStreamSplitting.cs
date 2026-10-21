using Weir.Core.Json;

namespace Weir.Core.Rules;

/// <summary>Splits a probe's streams by type and reads each stream's usable index, for every other planning concern to key off.</summary>
public static partial class RemuxRules
{
    /// <summary>Attached fonts and similar; not part of <see cref="SplitStreams"/>.</summary>
    public static IReadOnlyList<ProbeStreamInfo> AttachmentStreams(ProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return probe.Streams.Where(MetadataStreams.IsAttachmentStream).ToList();
    }

    public static SplitProbeStreams SplitStreams(ProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var video = new List<ProbeStreamInfo>();
        var audio = new List<ProbeStreamInfo>();
        var subtitles = new List<ProbeStreamInfo>();
        foreach (var stream in probe.Streams)
        {
            var codecType = RulesJson.Lower(WireStrings.Strip(RulesJson.StrMethodTarget(stream.Get("codec_type"))));
            switch (codecType)
            {
                case "video":
                    video.Add(stream);
                    break;
                case "audio":
                    audio.Add(stream);
                    break;
                case "subtitle":
                    subtitles.Add(stream);
                    break;
                default:
                    break;
            }
        }

        return new SplitProbeStreams(SortByIndex(video), SortByIndex(audio), SortByIndex(subtitles));
    }

    /// <summary>
    /// <c>list.sort(key=lambda x: int(x.get("index", 0)))</c>: every key is computed first, and the
    /// sort is stable. Issue #537 item 6: a present-but-unreadable index (<c>null</c>, or text that
    /// is not a number) sorts as if it were 0 rather than failing the whole plan; the stream is
    /// dropped later, at the point a real index is actually needed.
    /// </summary>
    private static List<ProbeStreamInfo> SortByIndex(List<ProbeStreamInfo> streams)
    {
        var keys = streams.Select(s => RulesJson.TryInt(s.Get("index"), out var n) ? n : 0).ToList();
        return streams.Select((stream, i) => (stream, key: keys[i])).OrderBy(p => p.key).Select(p => p.stream).ToList();
    }

    private static int IndexOf(ProbeStreamInfo stream) => RulesJson.ToInt32(RulesJson.Int(RulesJson.Item(stream.Json, "index")));

    /// <summary>
    /// Issue #537 item 6: a stream with no usable <c>index</c> (missing, <c>null</c>, or not a
    /// number) is skipped rather than failing the whole plan.
    /// </summary>
    private static bool TryIndexOf(ProbeStreamInfo stream, out int index)
    {
        try
        {
            index = IndexOf(stream);
            return true;
        }
        catch (RulesInputException)
        {
            index = 0;
            return false;
        }
    }

    /// <summary>Streams with a usable index, paired with it, in the order given.</summary>
    private static IEnumerable<(ProbeStreamInfo Stream, int Index)> WithIndex(IEnumerable<ProbeStreamInfo> streams)
    {
        foreach (var stream in streams)
        {
            if (TryIndexOf(stream, out var index))
            {
                yield return (stream, index);
            }
        }
    }
}
