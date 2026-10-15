// A media folder bind-mounted from the host, as the Docker install page tells people to do: Weir runs as the ids WEIR_PUID and WEIR_PGID name,
// delivers files into the output folder, and applies the output settings (WEIR_CHOWN_OUTPUT, WEIR_FILE_MODE_OUTPUT, WEIR_DIR_MODE_OUTPUT)
// to what it delivers, while the settings for the watched and work folders (which the image ignores) change nothing there.
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { accessSync, chmodSync, constants, copyFileSync, existsSync, mkdirSync, readFileSync, statSync, utimesSync } from "node:fs";
import { join } from "node:path";

import { REMAPPED, asEnvironment } from "./ownership.mjs";
import { pollUntil } from "./poll.mjs";
import { createFirstAccount } from "./smoke-data.mjs";

const FILE_MODE = 0o664;
const FOLDER_MODE = 0o2775;
// The mode of a file or folder as `chmod` takes it: the permission bits and the setuid, setgid and sticky bits.
const PERMISSIONS = 0o7777;
const WORLD_WRITABLE = 0o777;

const sha256 = (path) => createHash("sha256").update(readFileSync(path)).digest("hex");
const minutesAgo = (minutes) => new Date(Date.now() - minutes * 60_000);

/** A two-second mpeg4 and AAC file whose audio is tagged Japanese, made by the image's own ffmpeg, the file the live audit passes through. */
const MAKE_FIXTURE =
  "ffmpeg -nostdin -hide_banner -loglevel error -f lavfi -i color=c=black:s=320x180:d=2 -f lavfi -i sine=frequency=440:duration=2 " +
  "-map 0:v -map 1:a -c:v mpeg4 -c:a aac -metadata:s:a:0 language=jpn -y /media/fixture/film.mkv";

function makeFolder(path) {
  mkdirSync(path);
  // The process's umask would narrow the mode mkdir was given; the server runs as another user and has to write here.
  chmodSync(path, WORLD_WRITABLE);
}

/** Waits for the job to end, and for the file to be where it was told to go and gone from where it came from. */
async function deliver({ client, source, delivered, relativePath }) {
  const id = await client.enqueuePassThrough(relativePath);
  const job = await pollUntil(
    async () => {
      const found = await client.job(id);
      if (found && ["failed", "cancelled"].includes(found.status)) throw new Error(`the job for ${relativePath} ${found.status}: ${found.last_error}`);
      return found?.status === "completed" ? found : undefined;
    },
    { what: `the pass-through of ${relativePath}`, ceilingMs: 90_000, intervalMs: 250 },
  );
  await pollUntil(async () => (existsSync(delivered) && !existsSync(source) ? true : undefined), {
    what: `${relativePath} reaching the output folder and leaving the watched one`,
    ceilingMs: 30_000,
    intervalMs: 250,
  });
  return job;
}

/**
 * The delivered file and the folder made for it belong to the ids the server runs as, with the modes the output settings name, the
 * bytes are the ones that went in, and a user of this machine who is not that account can read them.
 */
function expectDelivered({ delivered, fixtureHash, host }) {
  const file = statSync(delivered);
  assert.deepEqual([file.uid, file.gid], [REMAPPED.uid, REMAPPED.gid], `${delivered} does not belong to the ids the server runs as`);
  assert.equal((file.mode & PERMISSIONS).toString(8), FILE_MODE.toString(8), `${delivered} does not have WEIR_FILE_MODE_OUTPUT's mode`);
  assert.equal(sha256(delivered), fixtureHash, `${delivered} is not the file that was passed through`);

  const folder = statSync(join(delivered, ".."));
  assert.deepEqual([folder.uid, folder.gid], [REMAPPED.uid, REMAPPED.gid], "the folder made for the file does not belong to the ids the server runs as");
  assert.equal((folder.mode & PERMISSIONS).toString(8), FOLDER_MODE.toString(8), "the folder made for the file does not have WEIR_DIR_MODE_OUTPUT's mode");

  // Readable by a user who is neither the server's account nor in its group, so the modes are what let this user in.
  assert.ok(host.uid !== REMAPPED.uid && host.gid !== REMAPPED.gid, "this smoke must not run as the ids it gives the server");
  accessSync(delivered, constants.R_OK);
  accessSync(join(delivered, ".."), constants.R_OK | constants.X_OK);
}

/**
 * SET-E38, PLAT-15, FL-29: two files are passed through into a bind-mounted output folder, one placed by the server's own account and one by
 * the user running this smoke, who is another user. Both arrive owned by the ids WEIR_PUID and WEIR_PGID name, with the output modes, and
 * the folders the settings for watched and work folders name are left as they were.
 * @param {string} world.mediaFolder A folder that does not exist yet, in a folder this user owns.
 */
export async function deliversIntoMediaFolder({ stack, client, candidate, mediaFolder }) {
  const host = { uid: process.getuid(), gid: process.getgid() };
  const [watched, work, output, fixtures] = ["watched", "work", "output", "fixture"].map((name) => join(mediaFolder, name));
  mkdirSync(mediaFolder);
  for (const folder of [watched, work, output, fixtures]) makeFolder(folder);

  const environment = {
    ...asEnvironment(REMAPPED),
    WEIR_CHOWN_OUTPUT: "true",
    WEIR_FILE_MODE_OUTPUT: FILE_MODE.toString(8),
    WEIR_DIR_MODE_OUTPUT: FOLDER_MODE.toString(8),
    WEIR_CHOWN_WATCHED: "true",
    WEIR_CHOWN_TEMP: "true",
    WEIR_DIR_MODE_WATCHED: "775",
    WEIR_DIR_MODE_TEMP: "775",
  };
  stack.up(candidate, "never", { environment, binds: [`${mediaFolder}:/media`] });
  stack.waitHealthy();

  const started = stack.logs();
  for (const name of ["WEIR_CHOWN_WATCHED", "WEIR_CHOWN_TEMP", "WEIR_DIR_MODE_WATCHED", "WEIR_DIR_MODE_TEMP"]) {
    assert.match(started, new RegExp(`${name} is set, but this image does not apply it to your watched or work folders\\. Ignoring it`));
  }
  assert.doesNotMatch(started, /(?:WEIR_CHOWN_OUTPUT|WEIR_FILE_MODE_OUTPUT|WEIR_DIR_MODE_OUTPUT) is set/, "the output settings the server applies were reported as ignored");

  await createFirstAccount({ client, stack });
  // A pass-through is asked for by a path under the watched folder of the movie workflow every install starts with, so that one is pointed at the media folder.
  const movies = (await client.libraries()).find((library) => library.media_type === "movie");
  assert.ok(movies, "a new install has no movie workflow");
  await client.updateLibrary(movies.id, {
    name: movies.name,
    media_type: "movie",
    watched_folder: "/media/watched",
    work_folder: "/media/work",
    output_folder: "/media/output",
    manager_connection_ids: movies.manager_connection_ids ?? [],
  });

  stack.exec(MAKE_FIXTURE);
  const fixture = join(fixtures, "film.mkv");
  const fixtureHash = sha256(fixture);

  // A file the server's own account placed in the watched folder.
  stack.exec("mkdir -p /media/watched/OwnerFilm && cp /media/fixture/film.mkv /media/watched/OwnerFilm/film.mkv && touch -d '10 minutes ago' /media/watched/OwnerFilm/film.mkv");
  // A file another user placed there: the server can read it and remove it, but it is not the server's.
  const otherSource = join(watched, "OtherFilm", "film.mkv");
  makeFolder(join(watched, "OtherFilm"));
  copyFileSync(fixture, otherSource);
  chmodSync(otherSource, 0o644);
  utimesSync(otherSource, minutesAgo(10), minutesAgo(10));
  const placed = statSync(otherSource);
  assert.deepEqual([placed.uid, placed.gid], [host.uid, host.gid]);

  for (const folder of ["OwnerFilm", "OtherFilm"]) {
    const delivered = join(output, folder, "film.mkv");
    await deliver({ client, source: join(watched, folder, "film.mkv"), delivered, relativePath: `${folder}/film.mkv` });
    expectDelivered({ delivered, fixtureHash, host });
  }

  for (const folder of [watched, work]) {
    const info = statSync(folder);
    assert.deepEqual([info.uid, info.gid], [host.uid, host.gid], `${folder} changed hands although the settings for it are ignored`);
    assert.equal(info.mode & PERMISSIONS, WORLD_WRITABLE, `${folder} changed mode although the settings for it are ignored`);
  }
  assert.doesNotMatch(stack.logs(), /could not apply the output ownership policy/, "the server could not apply the output settings to a file or folder");
}
