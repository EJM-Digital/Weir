// Real NTFS volumes for the scenarios: a virtual disk (a VHDX) made and attached with diskpart, which a hosted Windows runner has although it
// has no Hyper-V cmdlets, formatted and given a drive letter. A second volume is what makes "another drive" real; a small fixed one is what can
// really be filled. The runner is an administrator and a throwaway machine, so nothing here asks for more.
import { execFile } from "node:child_process";
import { mkdir, rm, writeFile, open } from "node:fs/promises";
import { join } from "node:path";

import { pollUntil } from "../docker-smoke/poll.mjs";
import { exists, freeBytes } from "./files-media.mjs";
import { json } from "./windows-machine.mjs";

const DISKPART_CEILING_MS = 180_000;
const MOUNT_CEILING_MS = 60_000;
const RELEASE_CEILING_MS = 30_000;
const MEGABYTE = 1024 * 1024;
const CHUNK = 4 * MEGABYTE;
// Drive letters are taken from the end of the alphabet, away from the ones a runner uses.
const LETTERS = "ZYXWVUTSRQPONM";

/** The diskpart script that makes a fixed-size virtual disk, formats it NTFS and gives it a drive letter. */
export function createScript({ file, sizeMb, letter, label }) {
  if (!/^[A-Z]$/.test(letter)) throw new Error(`"${letter}" is not a drive letter.`);
  if (!Number.isInteger(sizeMb) || sizeMb < 16) throw new Error(`A volume of ${sizeMb} MB is too small to format.`);
  if (file.includes('"')) throw new Error("The virtual disk's path cannot hold a quote.");
  return [
    `create vdisk file="${file}" maximum=${sizeMb} type=fixed`,
    `select vdisk file="${file}"`,
    "attach vdisk",
    "create partition primary",
    `format fs=ntfs quick label="${label}"`,
    `assign letter=${letter}`,
    "",
  ].join("\r\n");
}

/** The diskpart script that lets go of a virtual disk. */
export function detachScript({ file }) {
  return [`select vdisk file="${file}"`, "detach vdisk", ""].join("\r\n");
}

/** The first letter of the preferred ones that none of the drives in use has. */
export function freeLetter(inUse) {
  const taken = new Set(inUse.map((letter) => letter.toUpperCase()));
  const letter = [...LETTERS].find((candidate) => !taken.has(candidate));
  if (!letter) throw new Error("Every drive letter this scenario may use is taken.");
  return letter;
}

function diskpart(scriptFile) {
  return new Promise((resolve, reject) => {
    execFile("diskpart.exe", ["/s", scriptFile], { timeout: DISKPART_CEILING_MS, windowsHide: true }, (error, stdout, stderr) => {
      if (error) reject(new Error(`diskpart failed: ${error.message}\n${stdout}\n${stderr}`.trim(), { cause: error }));
      else resolve(stdout);
    });
  });
}

/** One virtual disk made for a scenario; `root` is its drive, such as `Z:\`. */
export class Volume {
  constructor({ file, letter, sizeMb, scratch, clock }) {
    this.file = file;
    this.letter = letter;
    this.sizeMb = sizeMb;
    this.scratch = scratch;
    this.clock = clock;
    this.root = `${letter}:\\`;
  }

  path(...parts) {
    return join(this.root, ...parts);
  }

  free() {
    return freeBytes(this.root);
  }

  /** Eats space with a file of zeros until this many bytes are free, and returns the file; it is what a scenario deletes to give the space back. */
  async leaveFree(bytes, name = "ballast.bin") {
    const ballast = this.path(name);
    let toWrite = (await this.free()) - bytes;
    if (toWrite < 0) throw new Error(`${this.root} has only ${await this.free()} bytes free, fewer than the ${bytes} to leave.`);
    const handle = await open(ballast, "w");
    try {
      const zeros = Buffer.alloc(CHUNK);
      while (toWrite > 0) {
        const part = Math.min(toWrite, CHUNK);
        await handle.write(zeros, 0, part);
        toWrite -= part;
      }
      await handle.sync();
    } finally {
      await handle.close();
    }
    return ballast;
  }

  /** Detaches the disk and deletes its file. */
  async remove() {
    const script = join(this.scratch, `detach-${this.letter}.txt`);
    await writeFile(script, detachScript({ file: this.file }));
    await diskpart(script).catch((error) => {
      if (!error.message.includes("already detached")) console.error(`::warning::${error.message}`);
    });
    // Windows lets go of the disk's file a moment after it is detached.
    await pollUntil(
      async () =>
        rm(this.file, { force: true }).then(
          () => true,
          () => undefined,
        ),
      { what: `the virtual disk ${this.file} to be released`, ceilingMs: RELEASE_CEILING_MS, intervalMs: 500, clock: this.clock },
    ).catch((error) => console.error(`::warning::${error.message}`));
  }
}

/** Makes virtual disks and removes them all at the end. */
export class Volumes {
  #made = [];

  constructor({ scratch, clock }) {
    this.scratch = scratch;
    this.clock = clock;
  }

  async create({ sizeMb, label = "WeirCI" }) {
    await mkdir(this.scratch, { recursive: true });
    const inUse = (await json(`ConvertTo-Json -Compress -InputObject @([IO.DriveInfo]::GetDrives() | ForEach-Object { $_.Name.Substring(0, 1) })`)) ?? [];
    const letter = freeLetter(inUse);
    const file = join(this.scratch, `volume-${letter}.vhdx`);
    const script = join(this.scratch, `create-${letter}.txt`);
    await writeFile(script, createScript({ file, sizeMb, letter, label }));
    const volume = new Volume({ file, letter, sizeMb, scratch: this.scratch, clock: this.clock });
    this.#made.push(volume);
    await diskpart(script);
    await pollUntil(async () => ((await exists(volume.root)) ? true : undefined), {
      what: `drive ${letter}: to appear`,
      ceilingMs: MOUNT_CEILING_MS,
      intervalMs: 500,
      clock: this.clock,
    });
    return volume;
  }

  async removeAll() {
    for (const volume of this.#made.splice(0).reverse()) await volume.remove();
  }
}

export const megabytes = (bytes) => bytes / MEGABYTE;
