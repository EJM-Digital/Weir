// A torrent over a folder of files, the way a tracker-less release is shared: no tracker, and a web seed (BEP 19) that names the server the
// files come from, so a client given the torrent downloads every byte over HTTP from there. Only Node's standard library.
import { createHash } from "node:crypto";
import fs from "node:fs";
import path from "node:path";

export const PIECE_BYTES = 256 * 1024;

/** The bencoding (BEP 3) of a number, string or buffer, list, or object with its keys in byte order. */
export function bencode(value) {
  if (Buffer.isBuffer(value)) return Buffer.concat([Buffer.from(`${value.length}:`), value]);
  if (typeof value === "number") return Buffer.from(`i${value}e`);
  if (typeof value === "string") return bencode(Buffer.from(value, "utf8"));
  if (Array.isArray(value)) return Buffer.concat([Buffer.from("l"), ...value.map(bencode), Buffer.from("e")]);
  if (value && typeof value === "object") {
    const keys = Object.keys(value).map((key) => Buffer.from(key, "utf8")).sort(Buffer.compare);
    return Buffer.concat([Buffer.from("d"), ...keys.flatMap((key) => [bencode(key), bencode(value[key.toString("utf8")])]), Buffer.from("e")]);
  }
  throw new TypeError(`cannot bencode ${typeof value}`);
}

const sha1 = (bytes) => createHash("sha1").update(bytes).digest();

/** Every file under a folder, as the path below it with forward slashes, in byte order. */
export function releaseFiles(folder) {
  const found = [];
  const walk = (directory) => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const full = path.join(directory, entry.name);
      if (entry.isDirectory()) walk(full);
      else found.push(path.relative(folder, full).split(path.sep).join("/"));
    }
  };
  walk(folder);
  return found.sort((a, b) => Buffer.compare(Buffer.from(a), Buffer.from(b)));
}

/**
 * The torrent of a release folder. `name` is the folder's name in the torrent; the web seed answers `<webSeedBase><name>/<path>` for each file.
 * @returns {{torrent: Buffer, infohash: string, total: number, files: {path: string, size: number}[]}}
 */
export function makeTorrent({ folder, name, webSeedBase }) {
  const relative = releaseFiles(folder);
  const entries = [];
  const pieces = [];
  let pending = Buffer.alloc(0);
  let total = 0;
  for (const file of relative) {
    const data = fs.readFileSync(path.join(folder, file));
    entries.push({ length: data.length, path: file.split("/") });
    total += data.length;
    pending = pending.length > 0 ? Buffer.concat([pending, data]) : data;
    while (pending.length >= PIECE_BYTES) {
      pieces.push(sha1(pending.subarray(0, PIECE_BYTES)));
      pending = pending.subarray(PIECE_BYTES);
    }
  }
  if (pending.length > 0) pieces.push(sha1(pending));
  const info = { name, "piece length": PIECE_BYTES, pieces: Buffer.concat(pieces), files: entries };
  return {
    torrent: bencode({ info, "url-list": [webSeedBase], comment: "Weir scenario release" }),
    infohash: sha1(bencode(info)).toString("hex"),
    total,
    files: entries.map((entry) => ({ path: entry.path.join("/"), size: entry.length })),
  };
}
