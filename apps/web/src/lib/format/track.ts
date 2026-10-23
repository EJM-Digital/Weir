/**
 * Media tracks in words: "Audio 2 · French · 5.1 E-AC-3", "Subtitle 1 · English · PGS image". Every screen that
 * names a track goes through here, so a file reads the same on Library, Activity and Choose tracks.
 */
import { PROCESSING_STREAM_LANGUAGE_OPTIONS } from "../processing/stream-language-options";

/** The bibliographic spellings some files carry, mapped to the ones Weir's language list uses. */
const LANGUAGE_ALIASES: Record<string, string> = {
  ger: "deu",
  fra: "fre",
  chi: "zho",
  dut: "nld",
  cze: "ces",
  gre: "ell",
  rum: "ron",
  may: "msa",
};

/** What a file says when it does not say which language a track is in. */
const UNDETERMINED = new Set(["", "und", "unk", "mis", "zxx"]);

const UNKNOWN_LANGUAGE = "Unknown language";

const AUDIO_CODECS: Record<string, string> = {
  eac3: "E-AC-3",
  ac3: "AC-3",
  aac: "AAC",
  dts: "DTS",
  truehd: "TrueHD",
  flac: "FLAC",
  opus: "Opus",
  mp3: "MP3",
  mp2: "MP2",
  vorbis: "Vorbis",
  pcm_s16le: "PCM",
  pcm_s24le: "PCM",
};

const VIDEO_CODECS: Record<string, string> = {
  h264: "H.264",
  hevc: "HEVC",
  h265: "HEVC",
  av1: "AV1",
  vp9: "VP9",
  mpeg2video: "MPEG-2",
  mpeg4: "MPEG-4",
  vc1: "VC-1",
};

/** Subtitles are either text a player draws or pictures it shows, and that is what decides how they behave. */
const SUBTITLE_TYPES: Record<string, string> = {
  subrip: "SRT text",
  srt: "SRT text",
  ass: "ASS text",
  ssa: "SSA text",
  webvtt: "WebVTT text",
  mov_text: "MP4 text",
  text: "Text",
  hdmv_pgs_subtitle: "PGS image",
  pgssub: "PGS image",
  dvd_subtitle: "DVD image",
  dvdsub: "DVD image",
  dvb_subtitle: "DVB image",
};

const CHANNEL_LAYOUTS: Record<string, string> = {
  "1": "mono",
  "2": "2.0",
  "6": "5.1",
  "8": "7.1",
};

const TRACK_KINDS: Record<string, string> = {
  video: "Video",
  audio: "Audio",
  subtitle: "Subtitle",
};

/** What a table holds for a key, or nothing: a name every object has (`constructor`, `toString`) is not an entry. */
function entry<V>(table: Record<string, V>, key: string): V | undefined {
  return Object.hasOwn(table, key) ? table[key] : undefined;
}

/** "fre" or "ger" as "French" or "German"; a name the file already spells out is kept as it is. */
export function languageName(code: string | null | undefined): string {
  const raw = (code ?? "").trim();
  const lower = raw.toLowerCase();
  if (UNDETERMINED.has(lower)) return UNKNOWN_LANGUAGE;
  const wanted = entry(LANGUAGE_ALIASES, lower) ?? lower;
  const hit = PROCESSING_STREAM_LANGUAGE_OPTIONS.find((o) => o.code === wanted);
  return hit ? hit.label : raw;
}

/** A channel count as the layout people know it: 6 is "5.1". */
export function channelLayout(channels: number): string {
  return entry(CHANNEL_LAYOUTS, String(channels)) ?? `${channels} channels`;
}

export function audioCodecName(codec: string): string {
  return entry(AUDIO_CODECS, codec.toLowerCase()) ?? codec.toUpperCase();
}

export function videoCodecName(codec: string): string {
  return entry(VIDEO_CODECS, codec.toLowerCase()) ?? codec.toUpperCase();
}

/** "hdmv_pgs_subtitle" as "PGS image": whether it is text or pictures, and which kind. */
export function subtitleTypeName(codec: string): string {
  return entry(SUBTITLE_TYPES, codec.toLowerCase()) ?? codec.toUpperCase();
}

/** The facts about a track that its description is built from, whichever API they came from. */
export interface DescribedTrack {
  type: string;
  language?: string | null;
  codec?: string | null;
  channels?: number | null;
  title?: string | null;
  forced?: boolean;
}

function formatOf(track: DescribedTrack): string {
  const codec = track.codec ?? "";
  if (track.type === "audio") {
    const layout = track.channels ? channelLayout(track.channels) : "";
    return [layout, codec ? audioCodecName(codec) : ""]
      .filter(Boolean)
      .join(" ");
  }
  if (track.type === "subtitle") return codec ? subtitleTypeName(codec) : "";
  if (track.type === "video") return codec ? videoCodecName(codec) : "";
  return codec;
}

/**
 * One track in words. `position` counts from 1 within its own kind, so the second audio track is "Audio 2"
 * however many video and subtitle tracks sit before it in the file.
 */
export function describeTrack(track: DescribedTrack, position: number): string {
  const kind = entry(TRACK_KINDS, track.type) ?? "Track";
  const parts = [`${kind} ${position}`];
  if (track.type !== "video") parts.push(languageName(track.language));
  parts.push(formatOf(track));
  if (track.forced) parts.push("Forced");
  if (track.title) parts.push(track.title);
  return parts.filter(Boolean).join(" · ");
}

/**
 * One track in words, placed by the counts positionsByKind made for the file it is in. A track those counts do not hold
 * is the first of its kind.
 */
export function describeTrackIn(
  track: DescribedTrack & { index: number },
  positions: ReadonlyMap<number, number>,
): string {
  return describeTrack(track, positions.get(track.index) ?? 1);
}

/** Each track's 1-based position among the tracks of its own kind, keyed by its index in the file. */
export function positionsByKind(
  tracks: readonly { index: number; type: string }[],
): Map<number, number> {
  const seen = new Map<string, number>();
  const positions = new Map<number, number>();
  for (const track of [...tracks].sort((a, b) => a.index - b.index)) {
    const next = (seen.get(track.type) ?? 0) + 1;
    seen.set(track.type, next);
    positions.set(track.index, next);
  }
  return positions;
}
