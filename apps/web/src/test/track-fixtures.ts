import type {
  ProcessingFileTrack,
  ProcessingFileTracks,
} from "../lib/processing/files-api";

type TrackFields = Pick<ProcessingFileTrack, "index" | "type"> &
  Partial<ProcessingFileTrack>;

/** One stream of a held file; what a test does not say is a stream the saved rules would keep. */
export function track(fields: TrackFields): ProcessingFileTrack {
  return {
    codec: null,
    language: null,
    title: null,
    channels: null,
    default: false,
    forced: false,
    rule_would_keep: true,
    rule_reason: "Kept by the saved rules.",
    ...fields,
  };
}

/**
 * The streams of a film held for a person to choose: the video, English and Japanese audio, and English and Spanish
 * subtitles, of which the saved rules keep the video, the English audio and the English subtitles.
 */
export function heldFileTracks(id = 7): ProcessingFileTracks {
  return {
    file_id: id,
    relative_path: "Film (2024)/film.mkv",
    media_scope: "movie",
    source_fingerprint: {
      device: 0,
      inode: 0,
      size_bytes: 4_000_000_000,
      modified_time_ns: 1,
    },
    streams: [
      track({ index: 0, type: "video", codec: "h264", default: true }),
      track({
        index: 1,
        type: "audio",
        codec: "eac3",
        language: "eng",
        channels: 6,
        default: true,
      }),
      track({
        index: 2,
        type: "audio",
        codec: "aac",
        language: "jpn",
        channels: 2,
        rule_would_keep: false,
        rule_reason: "Removed: not in a language you keep.",
      }),
      track({ index: 3, type: "subtitle", codec: "subrip", language: "eng" }),
      track({
        index: 4,
        type: "subtitle",
        codec: "subrip",
        language: "spa",
        rule_would_keep: false,
        rule_reason: "Removed: not in a language you keep.",
      }),
    ],
  };
}
