#!/usr/bin/env node
// Records what a running Weir server answers to the requests the web app makes, for the web's request tests
// (apps/web/src/test/recorded). Nothing it writes is typed by hand: it starts the built server on a scratch folder,
// makes the requests as the web app does, and keeps each answer's status and body as the server sent them.
//
// Usage (from the repository root, after `dotnet build apps/server/src/Weir.Host -c Release`):
//   node apps/web/scripts/record-replies.mjs --work C:/weir-recorded-replies
//
// Options:
//   --work <folder>    Scratch folder for the server's data and the test media. Created, and removed when done.
//                      Its path appears in some recorded answers, so use a short, neutral one.
//   --server <file>    The built server, Weir.dll. Defaults to the Release build of apps/server/src/Weir.Host.
//   --ffmpeg <file>    FFmpeg, to make the two small test videos. Defaults to `ffmpeg` on the PATH.
//   --port <number>    Where the server listens on localhost. Defaults to 18911.
//
// The server is stopped by the process id this script started, never by name. The account it makes lives only in the
// scratch folder, with a password made fresh for each run.
import { spawn, spawnSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import { existsSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "..", "..", "..");

function option(name, fallback) {
  const at = process.argv.indexOf(`--${name}`);
  return at >= 0 ? process.argv[at + 1] : fallback;
}

const work = path.resolve(option("work", ""));
if (!option("work")) {
  console.error(
    "Give a scratch folder with --work, for example --work C:/weir-recorded-replies.",
  );
  process.exit(2);
}
const serverDll = option(
  "server",
  path.join(repoRoot, "apps/server/src/Weir.Host/bin/Release/net10.0/Weir.dll"),
);
const ffmpeg = option("ffmpeg", "ffmpeg");
const port = Number(option("port", "18911"));
const outDir = path.join(here, "..", "src", "test", "recorded");
const base = `http://localhost:${port}`;
const folder = (name) => path.join(work, "media", name).replaceAll("\\", "/");

/** What the server said, by `METHOD /path` (or a name where one route is asked more than one way). */
const recordings = new Map();
let cookies = {};

function keepCookies(response) {
  for (const header of response.headers.getSetCookie()) {
    const pair = header.split(";")[0];
    const at = pair.indexOf("=");
    cookies[pair.slice(0, at)] = pair.slice(at + 1);
  }
}

const cookieHeader = () =>
  Object.entries(cookies)
    .map(([name, value]) => `${name}=${value}`)
    .join("; ");

/** One request as the web app sends it: the token first, then the call with the token in its JSON body. */
async function send(method, route, body, name, tokenIn = "body") {
  const headers = {
    "X-Requested-With": "XMLHttpRequest",
    Accept: "application/json",
  };
  let payload;
  if (method !== "GET") {
    const tokenResponse = await fetch(`${base}/api/v1/auth/csrf`, {
      headers: { ...headers, Cookie: cookieHeader() },
    });
    keepCookies(tokenResponse);
    const { csrf_token } = await tokenResponse.json();
    if (tokenIn === "header") {
      headers["X-CSRF-Token"] = csrf_token;
    } else {
      headers["Content-Type"] = "application/json";
      payload = JSON.stringify({ ...body, csrf_token });
    }
  }
  const response = await fetch(`${base}${route}`, {
    method,
    headers: { ...headers, Cookie: cookieHeader() },
    body: payload,
  });
  keepCookies(response);
  const text = await response.text();
  const answer = {
    status: response.status,
    body: text ? JSON.parse(text) : null,
  };
  if (name !== false) {
    recordings.set(name ?? `${method} ${route}`, answer);
    console.log(
      String(answer.status).padEnd(4),
      method.padEnd(7),
      (name ?? route).slice(0, 110),
    );
  }
  return answer;
}

const read = (route, name) => send("GET", route, undefined, name);
const write = (method, route, body, name, tokenIn) =>
  send(method, route, body ?? {}, name, tokenIn);

/** Polls until the check passes; the server's own queue decides when, and a ceiling ends a hang. */
async function until(check, what) {
  for (let attempt = 0; attempt < 120; attempt += 1) {
    if (await check()) return;
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
  throw new Error(`Gave up waiting for ${what}.`);
}

/**
 * A small test video with an English and a French track. The long one is raw video, which FFmpeg writes without encoding, so
 * it is two and a half gigabytes that Weir takes a few seconds to copy: long enough for the stream to report its progress.
 */
function makeVideo(file, { long = false } = {}) {
  mkdirSync(path.dirname(file), { recursive: true });
  const run = spawnSync(
    ffmpeg,
    [
      "-v",
      "error",
      "-y",
      "-f",
      "lavfi",
      "-i",
      long
        ? "testsrc=size=640x360:rate=25:duration=300"
        : "testsrc=size=64x64:rate=5:duration=2",
      "-f",
      "lavfi",
      "-i",
      `sine=frequency=440:duration=${long ? 300 : 2}`,
      "-f",
      "lavfi",
      "-i",
      `sine=frequency=880:duration=${long ? 300 : 2}`,
      "-map",
      "0:v",
      "-map",
      "1:a",
      "-map",
      "2:a",
      ...(long
        ? ["-c:v", "rawvideo", "-pix_fmt", "yuv420p"]
        : ["-c:v", "libx264", "-preset", "ultrafast"]),
      "-c:a",
      "aac",
      "-b:a",
      "16k",
      "-metadata:s:a:0",
      "language=eng",
      "-metadata:s:a:1",
      "language=fra",
      file,
    ],
    { stdio: "inherit" },
  );
  if (run.status !== 0)
    throw new Error("FFmpeg could not make the test video.");
}

/**
 * Listens to the Activity stream from now on and keeps the first few frames of each kind it sends, as it sent them: the
 * stream says the same thing every second, so a few of each show its shape without a file of repeats.
 */
function watchStream() {
  const frames = [];
  const kept = new Map();
  const controller = new AbortController();

  function keep(block) {
    const fields = Object.fromEntries(
      block
        .split("\n")
        .filter((line) => /^(event|data): /.test(line))
        .map((line) => [
          line.slice(0, line.indexOf(":")),
          line.slice(line.indexOf(":") + 2),
        ]),
    );
    if (!fields.event || !fields.data) return;
    const data = JSON.parse(fields.data);
    const kind =
      fields.event === "data.changed"
        ? `${fields.event} ${data.topic}`
        : fields.event === "processing.progress" &&
            Array.isArray(data.files) &&
            data.files.length > 0
          ? `${fields.event} with a file`
          : fields.event;
    // A progress frame with no file in it says nothing of its shape, so only a frame that carries a file is kept in numbers.
    const carriesFile = Array.isArray(data.files) && data.files.length > 0;
    const limit =
      fields.event === "processing.progress" && carriesFile ? 10 : 2;
    if ((kept.get(kind) ?? 0) >= limit) return;
    kept.set(kind, (kept.get(kind) ?? 0) + 1);
    frames.push({ event: fields.event, data });
  }

  const listening = (async () => {
    const response = await fetch(`${base}/api/v1/activity/stream`, {
      headers: { Cookie: cookieHeader(), Accept: "text/event-stream" },
      signal: controller.signal,
    });
    const decoder = new TextDecoder();
    let pending = "";
    try {
      for await (const chunk of response.body) {
        pending += decoder.decode(chunk, { stream: true });
        for (
          let end = pending.indexOf("\n\n");
          end >= 0;
          end = pending.indexOf("\n\n")
        ) {
          keep(pending.slice(0, end));
          pending = pending.slice(end + 2);
        }
      }
    } catch (error) {
      if (error.name !== "AbortError") throw error;
    }
  })();

  return {
    async stop() {
      controller.abort();
      await listening;
      recordings.set("GET /api/v1/activity/stream", {
        status: 200,
        body: { frames },
      });
    },
  };
}

async function record() {
  const password = `T${randomBytes(12).toString("base64url")}!9`;
  await write(
    "POST",
    "/api/v1/auth/bootstrap",
    { username: "recorded-admin", password },
    false,
  );
  await read("/api/v1/auth/me");
  const stream = watchStream();

  // The workflow and its folders; the small test file would otherwise be skipped for being under the minimum size.
  const [movies] = (await read("/api/v1/processing/libraries")).body;
  const {
    id,
    display_order,
    effective_max_concurrent_files,
    manager_coverage,
    manager_coverage_detail,
    discovered_from_connection_id,
    discovered_library_key,
    folders_synced_from_connection_id,
    active_job_count,
    next_look_at,
    periodic_scan,
    next_scan_at,
    updated_at,
    ...settings
  } = movies;
  await write("PUT", `/api/v1/processing/libraries/${id}`, {
    ...settings,
    min_file_size_mb: 0,
    ready_after_seconds: 1,
    watched_folder: folder("watched"),
    work_folder: folder("work"),
    output_folder: folder("output"),
  });
  await write("PUT", `/api/v1/processing/libraries/${id}/library-settings`, {
    library_folders: [folder("library")],
  });
  await write("POST", `/api/v1/processing/libraries/${id}/library-scan`);
  await until(
    async () =>
      (await read(`/api/v1/processing/libraries/${id}/library-overview`, false))
        .body.scan?.status === "completed",
    "the library scan",
  );

  await write(
    "POST",
    "/api/v1/processing/jobs/watched-folder-remux-scan-dispatch/enqueue",
    {
      enqueue_remux_jobs: true,
      media_scope: "movie",
      library_id: id,
    },
  );
  await until(async () => {
    const { files } = (await read("/api/v1/processing/files", false)).body;
    return (
      files.length > 0 && files.every((file) => file.status === "processed")
    );
  }, "the watched file to be processed");

  // What the screens read.
  await read("/api/v1/processing/files");
  await read(
    "/api/v1/processing/files?library_id=1&file_status=processed&path_contains=Recorded&within_days=30&limit=50&sort=when&direction=desc",
  );
  const fileId = (await read("/api/v1/processing/files", false)).body.files[0]
    .id;
  await read(`/api/v1/processing/files/${fileId}/remove-options`);
  await read(`/api/v1/processing/files/${fileId}/why-held`);
  await read(`/api/v1/processing/files/${fileId}/log`);
  await read(`/api/v1/processing/files/${fileId}/tracks`);
  await read("/api/v1/processing/files-at-once");
  await read("/api/v1/processing/files/rejected/summary");
  await read("/api/v1/processing/files/rejected/summary?library_id=1");
  await read("/api/v1/processing/kept-files");
  await read("/api/v1/processing/libraries");
  await read("/api/v1/processing/rule-sets");
  await read("/api/v1/processing/direct-play/devices");
  await write("PUT", "/api/v1/processing/direct-play/devices", {
    selected: ["apple_tv_4k"],
  });
  await read("/api/v1/processing/libraries/1/folder-chain");
  await read("/api/v1/processing/reject-support");
  await read(
    "/api/v1/processing/manager-setup?media_type=movie&watched_folder=" +
      encodeURIComponent(folder("watched")) +
      "&output_folder=" +
      encodeURIComponent(folder("output")) +
      "&remove_original_after_success=true",
  );
  await read("/api/v1/processing/library-suggestions");
  await read("/api/v1/processing/maintenance");
  await read("/api/v1/processing/operator-settings");
  await read("/api/v1/processing/libraries/1/library-settings");
  await read("/api/v1/processing/libraries/1/library-redownloads");
  await read("/api/v1/processing/libraries/1/library-overview");
  await read("/api/v1/processing/libraries/1/library-files");
  await read(
    "/api/v1/processing/libraries/1/library-files?status=needs_cleaning&q=Library&sort=size&direction=desc&page_size=25",
  );
  await write("POST", "/api/v1/processing/library-check", {
    movie_watched_folder: folder("watched"),
    movie_output_folder: folder("output"),
  });

  // A refusal the server gives for a file it does not know: the shape every refused request comes back in.
  await read("/api/v1/processing/files/9999/why-held");

  // Library files: the clean asks to be confirmed, then runs.
  const libraryFile = (
    await read("/api/v1/processing/libraries/1/library-files", false)
  ).body.files[0].path;
  await write(
    "POST",
    "/api/v1/processing/libraries/1/library-files/leave-alone",
    { path: libraryFile, leave_alone: true },
  );
  await write(
    "POST",
    "/api/v1/processing/libraries/1/library-files/leave-alone",
    { path: libraryFile, leave_alone: false },
    "POST /api/v1/processing/libraries/1/library-files/leave-alone (undone)",
  );
  await write(
    "POST",
    "/api/v1/processing/libraries/1/library-files/clean",
    { paths: [libraryFile], confirm_final_removal: false },
    "POST /api/v1/processing/libraries/1/library-files/clean (to confirm)",
  );
  await write("POST", "/api/v1/processing/libraries/1/library-files/clean", {
    paths: [libraryFile],
    confirm_final_removal: true,
  });
  await until(
    async () =>
      (await read("/api/v1/processing/library-cleans", false)).body.cleans
        .length > 0,
    "the library clean",
  );
  await read("/api/v1/processing/library-cleans");
  await read(
    "/api/v1/processing/library-cleans?library_id=1&path_contains=Library&within_days=7&limit=20",
  );
  await write("POST", "/api/v1/processing/libraries/1/library-schedule", {
    enabled: false,
    confirm_final_removal: false,
  });
  await write("POST", "/api/v1/processing/libraries/1/library-redownloads", {
    path: libraryFile,
    confirm_destructive: true,
  });
  await read("/api/v1/activity/recent?limit=10");
  await read(
    "/api/v1/activity/recent?result=success&library_id=1&about=files&limit=20&current_only=true&with_total=false",
  );

  // Previewing rules on a file, by its place in the watched folder and by its full path.
  await write(
    "POST",
    "/api/v1/processing/libraries/1/preview",
    { absolute_path: libraryFile },
    "POST /api/v1/processing/libraries/1/preview (full path)",
  );
  await write(
    "POST",
    "/api/v1/processing/libraries/1/preview",
    { relative_path: "Recorded Film (2020).mkv" },
    "POST /api/v1/processing/libraries/1/preview (in the watched folder)",
  );

  // Rule sets (profiles).
  const sets = (await read("/api/v1/processing/rule-sets", false)).body;
  const {
    id: firstSet,
    used_by_library_count,
    updated_at: setUpdated,
    ...ruleSet
  } = sets[0];
  const created = await write("POST", "/api/v1/processing/rule-sets", {
    ...ruleSet,
    name: "Recorded profile",
    primary_audio_lang: "fre",
    secondary_audio_lang: "eng",
    subtitle_mode: "keep_listed",
    subtitle_langs_csv: "eng,fre",
  });
  const setId = created.body.id;
  await write("PUT", `/api/v1/processing/rule-sets/${setId}`, {
    ...ruleSet,
    name: "Recorded profile",
    remove_commentary: false,
  });
  await write("DELETE", `/api/v1/processing/rule-sets/${setId}`);
  await write(
    "DELETE",
    `/api/v1/processing/rule-sets/${firstSet}`,
    undefined,
    "DELETE /api/v1/processing/rule-sets/1 (in use)",
  );

  // Workflows.
  const extra = await write("POST", "/api/v1/processing/libraries", {
    name: "Recorded extra",
    media_type: "movie",
  });
  const extraId = extra.body.id;
  await write("POST", "/api/v1/processing/libraries/reorder", {
    library_ids_in_order: [extraId, 1, 2],
  });
  await write("POST", `/api/v1/processing/libraries/${extraId}/unlink`);
  await write("DELETE", `/api/v1/processing/libraries/${extraId}`);

  // Notification channels.
  const channel = await write("POST", "/api/v1/suite/notification-channels", {
    label: "Recorded webhook",
    provider: "webhook",
    url: "https://hooks.example.invalid/weir",
    events: ["job_failed"],
    enabled: true,
  });
  await write("PUT", `/api/v1/suite/notification-channels/${channel.body.id}`, {
    label: "Recorded webhook",
    provider: "webhook",
    url: "https://hooks.example.invalid/weir",
    events: ["job_completed", "job_failed"],
    enabled: false,
  });
  await write(
    "POST",
    `/api/v1/suite/notification-channels/${channel.body.id}/test`,
  );
  await read("/api/v1/suite/notification-channels");
  await write(
    "DELETE",
    `/api/v1/suite/notification-channels/${channel.body.id}`,
    undefined,
    undefined,
    "header",
  );

  // Download clients.
  const client = await write("POST", "/api/v1/download-clients/connections", {
    kind: "sabnzbd",
    name: "SABnzbd on localhost",
    base_url: "http://localhost:8085",
    api_key: "recorded-key",
    enabled: true,
    username: "",
    password: "",
  });
  await write("PUT", `/api/v1/download-clients/connections/${client.body.id}`, {
    name: "SABnzbd on localhost",
    base_url: "http://localhost:8085",
    enabled: false,
  });
  await write(
    "POST",
    `/api/v1/download-clients/connections/${client.body.id}/test`,
  );
  await read("/api/v1/download-clients/connections");
  await read("/api/v1/download-clients/suggestions?media_type=movie");
  await write(
    "DELETE",
    `/api/v1/download-clients/connections/${client.body.id}`,
  );

  // Media managers.
  const manager = await write("POST", "/api/v1/media-managers/connections", {
    kind: "deluno",
    name: "Deluno on localhost",
    base_url: "http://localhost:5099",
    api_key: "recorded-key",
    enabled: true,
    downloaded_scan_enabled: false,
  });
  const managerId = manager.body.id;
  await write("PUT", `/api/v1/media-managers/connections/${managerId}`, {
    name: "Deluno on localhost",
    nickname: "Recorded",
    base_url: "http://localhost:5099",
    enabled: true,
  });
  await write("POST", `/api/v1/media-managers/connections/${managerId}/test`);
  await write(
    "POST",
    `/api/v1/media-managers/connections/${managerId}/webhook-secret`,
  );
  await read("/api/v1/media-managers/connections");
  await read(`/api/v1/media-managers/connections/${managerId}/folder-chain`);
  await read(`/api/v1/processing/libraries/discover/${managerId}`);
  await read(`/api/v1/processing/libraries/discover/${managerId}/drift`);
  await write(
    "POST",
    `/api/v1/processing/libraries/discover/${managerId}/import`,
    { keys: ["movies"] },
  );
  await read(`/api/v1/processing/reject-support?connection_ids=${managerId}`);
  await read(
    `/api/v1/processing/reject-support?connection_ids=${managerId}&connection_ids=${managerId + 1}`,
  );
  await read(
    `/api/v1/processing/manager-setup?media_type=tv&watched_folder=${encodeURIComponent(folder("watched"))}&output_folder=${encodeURIComponent(folder("output"))}&remove_original_after_success=false&connection_ids=${managerId}&connection_ids=${managerId + 1}`,
  );
  await write(
    "POST",
    `/api/v1/media-managers/connections/${managerId}/unlink-workflows`,
  );
  await write(
    "POST",
    `/api/v1/media-managers/connections/${managerId}/give-up-reports`,
  );
  await write("DELETE", `/api/v1/media-managers/connections/${managerId}`);

  // A pass over one file, and the next look at the watched folder.
  await write("POST", "/api/v1/processing/jobs/file-remux-pass/enqueue", {
    relative_media_path: "Recorded Film (2020).mkv",
    media_scope: "movie",
    library_id: 1,
  });

  // Maintenance.
  await write("POST", "/api/v1/processing/maintenance/run", {
    family: "work_temp_stale_sweep",
    media_scope: "movie",
  });

  // What a person does to the file last, since it ends the file's record.
  await write("POST", `/api/v1/processing/files/${fileId}/move-to-top`);
  await write("POST", `/api/v1/processing/files/${fileId}/manual-plan`, {
    keep: [{ index: 1, default: true, forced: false }],
    order: [1],
  });
  await write("POST", `/api/v1/processing/files/${fileId}/requeue`);
  await write("POST", "/api/v1/processing/files/requeue", {
    file_ids: [fileId],
  });
  await write("POST", "/api/v1/processing/files/rejected/process-again", {
    library_id: 1,
  });
  await write("POST", "/api/v1/processing/kept-files/9999/process-again");
  await write(
    "DELETE",
    `/api/v1/processing/files/${fileId}`,
    { resolution: "retry" },
    "DELETE /api/v1/processing/files/1 (try again)",
  );
  await write("DELETE", `/api/v1/processing/files/${fileId}`);
  await stream.stop();
}

/** Stops the server this script started, by its process id, and waits for it to let go of the scratch folder. */
async function stopServer(child) {
  if (child.exitCode !== null) return;
  const exited = new Promise((resolve) => child.once("exit", resolve));
  child.kill();
  await exited;
}

async function main() {
  if (!existsSync(serverDll))
    throw new Error(`There is no built server at ${serverDll}.`);
  rmSync(work, { recursive: true, force: true });
  for (const name of ["watched", "work", "output", "library"])
    mkdirSync(folder(name), { recursive: true });
  makeVideo(path.join(folder("watched"), "Recorded Film (2020).mkv"), {
    long: true,
  });
  makeVideo(path.join(folder("library"), "Library Film (2019).mkv"));

  const child = spawn(
    "dotnet",
    [serverDll, "--host", "localhost", "--port", String(port), "--no-browser"],
    {
      cwd: work,
      stdio: "ignore",
      env: {
        ...process.env,
        WEIR_HOME: path.join(work, "home"),
        WEIR_SESSION_SECRET: randomBytes(48).toString("hex"),
        WEIR_ENV: "development",
      },
    },
  );
  try {
    await until(async () => {
      try {
        return (await fetch(`${base}/api/v1/auth/bootstrap/status`)).ok;
      } catch {
        return false;
      }
    }, "the server to start");
    await record();
    mkdirSync(outDir, { recursive: true });
    const files = {
      session: [],
      connections: [],
      activity: [],
      processing: [],
    };
    for (const [route, answer] of recordings) {
      const url = route.split(" ")[1];
      const group = url.includes("/auth/")
        ? "session"
        : /\/(media-managers|download-clients)\/|\/suite\/notification/.test(
              url,
            )
          ? "connections"
          : url.includes("/activity/")
            ? "activity"
            : "processing";
      files[group].push([route, answer]);
    }
    for (const [group, entries] of Object.entries(files)) {
      writeFileSync(
        path.join(outDir, `${group}.json`),
        `${JSON.stringify(Object.fromEntries(entries), null, 2)}\n`,
      );
    }
    console.log(`Recorded ${recordings.size} answers into ${outDir}.`);
  } finally {
    await stopServer(child);
    rmSync(work, { recursive: true, force: true });
  }
}

await main();
