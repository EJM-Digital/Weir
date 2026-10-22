using System.Globalization;
using System.Text.Json;
using Weir.Core.Json;
using Weir.Core.Rules;
using Weir.Core.Text;

namespace Weir.Core.Media;

/// <summary>
/// One track as <c>mkvmerge -J</c> reports it: its own id (what every <c>--...-tracks</c> and per-track
/// option addresses) and its type (<c>video</c>, <c>audio</c> or <c>subtitles</c>).
/// </summary>
public sealed record MkvmergeTrack(int Id, string Type);

/// <summary>
/// One attachment as <c>mkvmerge -J</c> reports it. Attachment ids are a separate numbering from track ids.
/// </summary>
/// <param name="Id">Its id, which <c>--attachments</c> addresses.</param>
/// <param name="ContentType">Its MIME type, e.g. <c>image/jpeg</c> for cover art or <c>font/ttf</c> for a font.</param>
/// <param name="FileName">Its file name, e.g. <c>cover.jpg</c>.</param>
public sealed record MkvmergeAttachment(int Id, string ContentType, string FileName)
{
    /// <summary>
    /// True for cover art rather than a font or similar: Matroska stores a poster as an attachment, and it is
    /// what ffprobe surfaces as a video stream flagged <c>attached_pic</c> — so this is the attachment that
    /// <see cref="Weir.Core.Rules.MetadataRules.RemoveImages"/> governs, while every other attachment is
    /// governed by <see cref="Weir.Core.Rules.MetadataRules.RemoveAttachments"/>.
    /// Matched on the MIME type first, then on the names the Matroska cover-art convention reserves
    /// (<c>cover</c>/<c>small_cover</c>, with the landscape and portrait variants).
    /// </summary>
    public bool IsCoverArt =>
        ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || CoverArtNames.Contains(Path.GetFileNameWithoutExtension(FileName));

    private static readonly HashSet<string> CoverArtNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cover", "small_cover", "cover_land", "small_cover_land",
    };
}

/// <summary>What <c>mkvmerge -J</c> says about a source file.</summary>
/// <param name="Tracks">Its tracks, in the order mkvmerge lists them (ascending id).</param>
/// <param name="Attachments">Its attachments, which are numbered separately from tracks.</param>
/// <param name="ChapterEntries">How many chapter entries the file carries, 0 when it has none.</param>
public sealed record MkvmergeIdentification(
    IReadOnlyList<MkvmergeTrack> Tracks,
    IReadOnlyList<MkvmergeAttachment> Attachments,
    int ChapterEntries);

/// <summary>
/// Raised when a source's mkvmerge tracks cannot be lined up with its ffprobe streams, so no plan built
/// against ffprobe indices can be addressed to mkvmerge safely. See
/// <see cref="MkvmergeCommands.MapStreamIndicesToTrackIds"/>: Weir refuses the write rather than guessing
/// and silently keeping the wrong tracks.
/// </summary>
public sealed class MkvmergeTrackMappingException(string message) : Exception(message);

/// <summary>
/// Reading <c>mkvmerge -J</c>'s identification JSON and lining its track ids up with ffprobe's stream indices
/// (see <see cref="MkvmergeCommands"/> for why the two numberings diverge).
/// </summary>
public static partial class MkvmergeCommands
{
    /// <summary>Reads what <see cref="BuildIdentifyArgv"/> printed.</summary>
    public static MkvmergeIdentification ParseIdentification(JsonElement json)
    {
        var tracks = new List<MkvmergeTrack>();
        var attachments = new List<MkvmergeAttachment>();
        var chapters = 0;
        if (json.ValueKind != JsonValueKind.Object)
        {
            return new MkvmergeIdentification(tracks, attachments, chapters);
        }

        var trackArray = RulesJson.Get(json, "tracks");
        if (trackArray is { ValueKind: JsonValueKind.Array })
        {
            foreach (var track in trackArray.Value.EnumerateArray())
            {
                if (track.ValueKind == JsonValueKind.Object
                    && RulesJson.TryInt(RulesJson.Get(track, "id"), out var id))
                {
                    tracks.Add(new MkvmergeTrack((int)id, RulesJson.Lower(WireStrings.Strip(RulesJson.StrOr(RulesJson.Get(track, "type"), string.Empty)))));
                }
            }
        }

        var attachmentArray = RulesJson.Get(json, "attachments");
        if (attachmentArray is { ValueKind: JsonValueKind.Array })
        {
            foreach (var attachment in attachmentArray.Value.EnumerateArray())
            {
                if (attachment.ValueKind == JsonValueKind.Object
                    && RulesJson.TryInt(RulesJson.Get(attachment, "id"), out var id))
                {
                    attachments.Add(new MkvmergeAttachment(
                        (int)id,
                        WireStrings.Strip(RulesJson.StrOr(RulesJson.Get(attachment, "content_type"), string.Empty)),
                        WireStrings.Strip(RulesJson.StrOr(RulesJson.Get(attachment, "file_name"), string.Empty))));
                }
            }
        }

        var chapterArray = RulesJson.Get(json, "chapters");
        if (chapterArray is { ValueKind: JsonValueKind.Array })
        {
            foreach (var entry in chapterArray.Value.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object && RulesJson.TryInt(RulesJson.Get(entry, "num_entries"), out var count))
                {
                    chapters += (int)count;
                }
            }
        }

        return new MkvmergeIdentification(tracks, attachments, chapters);
    }

    /// <summary>mkvmerge's type word for an ffprobe <c>codec_type</c>.</summary>
    private static string MkvmergeTypeFor(string ffprobeCodecType) => ffprobeCodecType switch
    {
        "video" => "video",
        "audio" => "audio",
        "subtitle" => "subtitles",
        _ => string.Empty,
    };

    /// <summary>
    /// Lines a source's ffprobe streams up with its mkvmerge tracks and returns ffprobe stream index →
    /// mkvmerge track id.
    /// <para>
    /// Only ffprobe streams that mkvmerge also counts as tracks take part: <c>video</c>, <c>audio</c> and
    /// <c>subtitle</c> streams that are not cover art (<c>disposition.attached_pic</c>), because Matroska
    /// stores cover art as an attachment, which mkvmerge numbers separately. ffprobe's own
    /// <c>attachment</c>-typed streams (fonts) are likewise attachments to mkvmerge and take no part.
    /// </para>
    /// <para>
    /// The two lists are then matched in order and their types compared. A disagreement means this file does
    /// not follow the correspondence above, so rather than write a file with the wrong tracks kept, this
    /// throws <see cref="MkvmergeTrackMappingException"/> and the caller falls back to ffmpeg.
    /// </para>
    /// </summary>
    /// <param name="streams">The source's ffprobe streams, in the order ffprobe listed them.</param>
    /// <param name="identification">What <c>mkvmerge -J</c> said about the same file.</param>
    public static IReadOnlyDictionary<int, int> MapStreamIndicesToTrackIds(
        IReadOnlyList<ProbeStreamInfo> streams,
        MkvmergeIdentification identification)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(identification);
        var mappable = new List<(int Index, string Type)>();
        foreach (var stream in streams)
        {
            var type = MkvmergeTypeFor(stream.CodecType);
            if (type.Length == 0 || IsMatroskaAttachment(stream) || stream.Index is not { } index)
            {
                continue;
            }

            mappable.Add(((int)index, type));
        }

        if (mappable.Count != identification.Tracks.Count)
        {
            throw new MkvmergeTrackMappingException(
                $"mkvmerge reported {Plural.Of(identification.Tracks.Count, "track")} but ffprobe reported "
                + $"{Plural.Of(mappable.Count, "matching stream")}, so a plan built from ffprobe indices "
                + "cannot be addressed to mkvmerge safely.");
        }

        var map = new Dictionary<int, int>(mappable.Count);
        for (var i = 0; i < mappable.Count; i++)
        {
            var (index, type) = mappable[i];
            var track = identification.Tracks[i];
            if (!string.Equals(track.Type, type, StringComparison.Ordinal))
            {
                throw new MkvmergeTrackMappingException(
                    $"ffprobe stream {index.ToString(CultureInfo.InvariantCulture)} is {type} but the mkvmerge track in the same "
                    + $"position (id {track.Id.ToString(CultureInfo.InvariantCulture)}) is {track.Type}, so the two numberings do not line up.");
            }

            map[index] = track.Id;
        }

        return map;
    }

    /// <summary>
    /// True for a video stream Matroska carries as an attachment rather than a track: cover art, which ffprobe
    /// reports flagged <c>attached_pic</c>. ffprobe's own <c>attachment</c> streams are not a track type to begin with.
    /// </summary>
    private static bool IsMatroskaAttachment(ProbeStreamInfo stream)
    {
        var disposition = stream.Get("disposition");
        if (!RulesJson.IsDict(disposition))
        {
            return false;
        }

        var attachedPic = RulesJson.Get(disposition!.Value, "attached_pic");
        return RulesJson.Truthy(attachedPic) && RulesJson.Int(attachedPic) != 0;
    }
}
