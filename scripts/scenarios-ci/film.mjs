// The film every scenario's release is a copy of, and what a cleaned copy of it must look like. The film is made by the ffmpeg in the
// image under test, and cleaned by that same ffmpeg, so these scenarios prove the image's own tools work.
import { posix } from "node:path";

import { shellQuote } from "../docker-smoke/shell.mjs";

const AUDIO = ["eng", "fre", "jpn"];
const SUBTITLES = ["eng", "spa"];
const TITLE = "Big Buck Bunny";
// How far the cleaned film's length may differ from the original's.
const LENGTH_TOLERANCE_SECONDS = 1.5;

const subtitlePath = (path, language) => `${path.slice(0, -posix.extname(path).length)}.${language}.srt`;

/** The arguments of the ffmpeg that makes the film: one video, an audio track for each language and a subtitle track for each. */
export function filmArguments({ path, seconds = 30, audio = AUDIO, subtitles = SUBTITLES, title = TITLE, videoBitrate = "3M" }) {
  const args = ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", `testsrc2=duration=${seconds}:size=640x360:rate=24`];
  audio.forEach((_, i) => args.push("-f", "lavfi", "-i", `sine=frequency=${220 * (i + 1)}:duration=${seconds}`));
  for (const language of subtitles) args.push("-i", subtitlePath(path, language));
  args.push("-map", "0:v");
  audio.forEach((_, i) => args.push("-map", `${i + 1}:a`));
  subtitles.forEach((_, i) => args.push("-map", `${audio.length + 1 + i}:s`));
  args.push("-c:v", "mpeg4", "-b:v", videoBitrate, "-c:a", "aac", "-c:s", "srt", "-metadata", `title=${title}`);
  audio.forEach((language, i) => args.push(`-metadata:s:a:${i}`, `language=${language}`));
  subtitles.forEach((language, i) => args.push(`-metadata:s:s:${i}`, `language=${language}`));
  args.push(path);
  return args;
}

/** The shell command, run in the container, that writes the subtitle files, makes the film from them and takes them away again. */
export function filmCommand({ path, seconds = 30, audio = AUDIO, subtitles = SUBTITLES, title = TITLE, videoBitrate = "3M" }) {
  if (title.includes("'")) throw new Error("A film's title cannot hold a quote.");
  const files = subtitles.map((language) => subtitlePath(path, language));
  const writes = subtitles.map(
    (language, i) =>
      `printf '1\\n00:00:01,000 --> 00:00:04,000\\n${title} (${language})\\n\\n2\\n00:00:05,000 --> 00:00:08,000\\nSubtitle two\\n' > ${shellQuote(files[i])}`,
  );
  const ffmpeg = ["ffmpeg", ...filmArguments({ path, seconds, audio, subtitles, title, videoBitrate }).map(shellQuote)].join(" ");
  return [`mkdir -p ${shellQuote(posix.dirname(path))}`, ...writes, ffmpeg, `rm -f ${files.map(shellQuote).join(" ")}`].join(" && ");
}

/** What is inside a media file, from ffprobe's JSON: the video tracks, the language of each audio and subtitle track, the length and the size. */
export function summarizeProbe({ streams, format }) {
  const languages = (type) => streams.filter((stream) => stream.codec_type === type).map((stream) => stream.tags?.language ?? "");
  return {
    video: streams.filter((stream) => stream.codec_type === "video").length,
    audio: languages("audio"),
    subtitles: languages("subtitle"),
    seconds: Number(format.duration),
    bytes: Number(format.size),
  };
}

/** What is wrong with the cleaned copy of the film, in words; nothing when it is as it must be. */
export function cleanedFilmProblems(original, cleaned) {
  const problems = [];
  if (cleaned.video !== original.video) problems.push(`the video is not kept (${cleaned.video} video track, the film had ${original.video})`);
  if (Math.abs(cleaned.seconds - original.seconds) >= LENGTH_TOLERANCE_SECONDS) {
    problems.push(`the film's length is not kept (${cleaned.seconds.toFixed(1)} s of ${original.seconds.toFixed(1)} s)`);
  }
  if (cleaned.audio.join(",") !== "eng") problems.push(`only the English audio should be kept (kept: ${cleaned.audio.join(",") || "none"}; it had ${original.audio.join(",")})`);
  if (cleaned.subtitles.length > 0) problems.push(`the subtitle tracks should be removed (kept: ${cleaned.subtitles.join(",")})`);
  if (cleaned.bytes >= original.bytes) problems.push(`the cleaned film should be smaller (${cleaned.bytes} bytes of ${original.bytes})`);
  return problems;
}

/** The shell command that makes a file with sound and no picture: uncompressed 24-bit stereo at 96 kHz, about 0.58 MB a second, so it is large enough to pass a size minimum. */
export function audioOnlyCommand({ path, seconds }) {
  const ffmpeg = ["ffmpeg", "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", `sine=frequency=440:duration=${seconds}`, "-ac", "2", "-ar", "96000", "-c:a", "pcm_s24le", path].map(shellQuote).join(" ");
  return `mkdir -p ${shellQuote(posix.dirname(path))} && ${ffmpeg}`;
}
