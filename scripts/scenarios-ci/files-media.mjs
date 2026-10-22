// Real media and real files for the windows-files scenarios: a film made by the ffmpeg the package carries, what ffprobe says is in it, and the
// bytes and folder trees the scenarios compare. Nothing here is a stand-in.
import { execFile } from "node:child_process";
import { createHash } from "node:crypto";
import { createReadStream } from "node:fs";
import { mkdir, readdir, rm, stat, statfs, writeFile } from "node:fs/promises";
import { dirname, join, relative } from "node:path";

import { filmArguments, summarizeProbe } from "./film.mjs";

const TOOL_CEILING_MS = 300_000;

/** ffmpeg and ffprobe as the package carries them, beside the server (`<server folder>\bin\ffmpeg`). */
export function packagedTools(serverFolder) {
  const folder = join(serverFolder, "bin", "ffmpeg");
  return { ffmpeg: join(folder, "ffmpeg.exe"), ffprobe: join(folder, "ffprobe.exe") };
}

function runTool(program, args) {
  return new Promise((resolve, reject) => {
    execFile(program, args, { timeout: TOOL_CEILING_MS, maxBuffer: 64 * 1024 * 1024, windowsHide: true }, (error, stdout, stderr) => {
      if (error) reject(new Error(`${program} failed: ${error.message}\n${stderr}`.trim(), { cause: error }));
      else resolve(stdout);
    });
  });
}

/** Makes a film with one video track and an audio track for each language, in its own folder. The subtitles are written, used and taken away. */
export async function makeFilm(tools, { path, seconds = 30, audio = ["eng", "fre", "jpn"], subtitles = [], videoBitrate = "3M", defaultAudio = false, title = "Big Buck Bunny" }) {
  await mkdir(dirname(path), { recursive: true });
  const stem = path.slice(0, -".mkv".length);
  const srt = subtitles.map((language) => `${stem}.${language}.srt`);
  for (const file of srt) await writeFile(file, `1\n00:00:01,000 --> 00:00:04,000\n${title}\n\n`);
  try {
    await runTool(tools.ffmpeg, filmArguments({ path, seconds, audio, subtitles, title, videoBitrate, defaultAudio }));
  } finally {
    await Promise.all(srt.map((file) => rm(file, { force: true })));
  }
}

/** What is inside a media file: video tracks, the language of each audio and subtitle track, the length and the size. */
export async function probe(tools, path) {
  return summarizeProbe(JSON.parse(await runTool(tools.ffprobe, ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path])));
}

export const sha256 = (path) =>
  new Promise((resolve, reject) => {
    const hash = createHash("sha256");
    createReadStream(path).on("data", (chunk) => hash.update(chunk)).on("error", reject).on("end", () => resolve(hash.digest("hex")));
  });

/** Every file under the folder, as full paths; empty when the folder is not there. */
export async function allFiles(folder) {
  let entries;
  try {
    entries = await readdir(folder, { withFileTypes: true });
  } catch {
    return [];
  }
  const found = [];
  for (const entry of entries) {
    const path = join(folder, entry.name);
    if (entry.isDirectory()) found.push(...(await allFiles(path)));
    else found.push(path);
  }
  return found;
}

/** Every file under the folder as a forward-slash path relative to it, in order. Folders Windows keeps at the top of a drive are left out. */
export async function treeOf(folder) {
  const files = (await allFiles(folder)).map((path) => relative(folder, path).replaceAll("\\", "/"));
  return files.filter((file) => !file.startsWith("System Volume Information/") && !file.startsWith("$RECYCLE.BIN/")).sort();
}

export const sizeOf = async (path) => (await stat(path)).size;
export const exists = (path) =>
  stat(path).then(
    () => true,
    () => false,
  );

/** Free bytes on the drive that holds the path, as a program that is allowed to write there would see them. */
export async function freeBytes(path) {
  const fs = await statfs(path);
  return Number(fs.bavail) * Number(fs.bsize);
}
