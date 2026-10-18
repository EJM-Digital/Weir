// The release source of the with-deluno shard (scenarios-ci/with-deluno/fixture): the torrent it makes is a torrent, the bytes it serves are the
// release's, and its feed is one a Torznab client can read. Run against the real server on a free port, with files in a temporary folder.
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { after, before, test } from "node:test";

import { createFixture, parseRange } from "./scenarios-ci/with-deluno/fixture/server.mjs";
import { PIECE_BYTES, bencode, makeTorrent, releaseFiles } from "./scenarios-ci/with-deluno/fixture/torrent.mjs";

/** Reads a bencoded value back, so the torrent is judged by something other than the code that wrote it. */
function bdecode(bytes) {
  let at = 0;
  const read = () => {
    const c = String.fromCharCode(bytes[at]);
    if (c === "i") {
      const end = bytes.indexOf(0x65, at);
      const n = Number(bytes.subarray(at + 1, end).toString());
      at = end + 1;
      return n;
    }
    if (c === "l" || c === "d") {
      at += 1;
      const items = [];
      while (bytes[at] !== 0x65) items.push(read());
      at += 1;
      if (c === "l") return items;
      return Object.fromEntries(Array.from({ length: items.length / 2 }, (_, i) => [items[2 * i].toString(), items[2 * i + 1]]));
    }
    const colon = bytes.indexOf(0x3a, at);
    const length = Number(bytes.subarray(at, colon).toString());
    const value = bytes.subarray(colon + 1, colon + 1 + length);
    at = colon + 1 + length;
    return value;
  };
  return read();
}

// --- the torrent --------------------------------------------------------------------------------------------------------

test("bencoding is that of BEP 3: strings, numbers, lists, and dictionaries with their keys in order", () => {
  assert.equal(bencode("spam").toString(), "4:spam");
  assert.equal(bencode(3).toString(), "i3e");
  assert.equal(bencode(["spam", "eggs"]).toString(), "l4:spam4:eggse");
  assert.equal(bencode({ cow: "moo", spam: "eggs" }).toString(), "d3:cow3:moo4:spam4:eggse");
  assert.equal(bencode({ spam: "eggs", cow: "moo" }).toString(), "d3:cow3:moo4:spam4:eggse");
  assert.throws(() => bencode(undefined), /cannot bencode/);
});

const work = mkdtempSync(join(tmpdir(), "weir-fixture-test-"));
after(() => rmSync(work, { recursive: true, force: true }));

function release(name, files) {
  const folder = join(work, "torrent", name);
  mkdirSync(folder, { recursive: true });
  for (const [file, bytes] of Object.entries(files)) writeFileSync(join(folder, file), bytes);
  return folder;
}

test("a torrent over a folder lists every file in order, hashes the files as one stream in pieces, and names the web seed", () => {
  const film = Buffer.alloc(PIECE_BYTES + 1000, 7);
  const nfo = Buffer.from("hello\n");
  const folder = release("Film.2020", { "Film.2020.mkv": film, "Film.2020.nfo": nfo });
  const made = makeTorrent({ folder, name: "Film.2020", webSeedBase: "http://fixture:8765/webseed/" });
  const decoded = bdecode(made.torrent);
  assert.equal(decoded["url-list"][0].toString(), "http://fixture:8765/webseed/");
  assert.equal(decoded.announce, undefined, "no tracker");
  assert.equal(decoded.info.name.toString(), "Film.2020");
  assert.equal(decoded.info["piece length"], PIECE_BYTES);
  assert.deepEqual(decoded.info.files.map((file) => [file.path.map(String).join("/"), file.length]), [["Film.2020.mkv", film.length], ["Film.2020.nfo", nfo.length]]);
  const stream = Buffer.concat([film, nfo]);
  const expected = Buffer.concat([0, PIECE_BYTES].map((start) => createHash("sha1").update(stream.subarray(start, start + PIECE_BYTES)).digest()));
  assert.deepEqual(decoded.info.pieces, expected);
  assert.equal(made.total, stream.length);
  assert.equal(made.infohash, createHash("sha1").update(bencode(decoded.info)).digest("hex"));
  assert.deepEqual(releaseFiles(folder), ["Film.2020.mkv", "Film.2020.nfo"]);
});

// --- the server ---------------------------------------------------------------------------------------------------------

test("a range asks for the bytes it names, to the end of the file, or the last bytes, and one past the end is refused", () => {
  assert.deepEqual(parseRange("bytes=0-99", 1000), { start: 0, end: 99 });
  assert.deepEqual(parseRange("bytes=900-", 1000), { start: 900, end: 999 });
  assert.deepEqual(parseRange("bytes=-100", 1000), { start: 900, end: 999 });
  assert.deepEqual(parseRange("bytes=990-5000", 1000), { start: 990, end: 999 });
  assert.equal(parseRange("bytes=1000-1010", 1000), "unsatisfiable");
  assert.equal(parseRange(undefined, 1000), null);
  assert.equal(parseRange("items=0-1", 1000), null);
});

let server;
let base;
const film = Buffer.from("not really a film, but bytes".repeat(20_000));

before(async () => {
  mkdirSync(join(work, "source"), { recursive: true });
  writeFileSync(join(work, "source", "x.mkv"), film);
  server = createFixture({ work, publicBase: "http://fixture:8765" });
  await new Promise((resolve) => server.listen(0, resolve));
  base = `http://localhost:${server.address().port}`;
});
after(() => server.close());

const post = (path, body) => fetch(base + path, { method: "POST", body: JSON.stringify(body) });

test("a release is made from a film in the work folder: its folder, its torrent, and what to tell Deluno", async () => {
  const made = await post("/releases", { source: join(work, "source", "x.mkv"), title: "ScnAb12Film", year: 2020 });
  assert.equal(made.status, 200);
  const body = await made.json();
  assert.equal(body.release, "ScnAb12Film.2020.1080p.WEB-DL.x264-WEIR");
  assert.equal(body.url, `http://fixture:8765/dl/${body.release}.torrent`);
  assert.equal(body.size, film.length + "Weir scenario release\n".length);
  assert.deepEqual(body.files.map((file) => file.path), [`${body.release}.mkv`, `${body.release}.nfo`]);

  const torrent = Buffer.from(await (await fetch(`${base}/dl/${body.release}.torrent`)).arrayBuffer());
  assert.equal(createHash("sha1").update(bencode(bdecode(torrent).info)).digest("hex"), body.infohash);

  const whole = await fetch(`${base}/webseed/${body.release}/${body.release}.mkv`);
  assert.deepEqual(Buffer.from(await whole.arrayBuffer()), film);
  const part = await fetch(`${base}/webseed/${body.release}/${body.release}.mkv`, { headers: { Range: "bytes=10-19" } });
  assert.equal(part.status, 206);
  assert.equal(part.headers.get("content-range"), `bytes 10-19/${film.length}`);
  assert.deepEqual(Buffer.from(await part.arrayBuffer()), film.subarray(10, 20));
  assert.equal((await fetch(`${base}/webseed/${body.release}/${body.release}.mkv`, { headers: { Range: `bytes=${film.length}-` } })).status, 416);
});

test("a second release of the same name, a film outside the work folder and a name that is not letters and digits are refused", async () => {
  assert.equal((await post("/releases", { source: join(work, "source", "x.mkv"), title: "ScnAb12Film", year: 2020 })).status, 409);
  assert.equal((await post("/releases", { source: "/etc/passwd", title: "Other", year: 2020 })).status, 400);
  assert.equal((await post("/releases", { source: join(work, "source", "x.mkv"), title: "../Other", year: 2020 })).status, 400);
});

test("nothing outside the releases folder can be fetched through the web seed", async () => {
  assert.equal((await fetch(`${base}/webseed/..%2Fsource%2Fx.mkv`)).status, 404);
  assert.equal((await fetch(`${base}/webseed/missing/file.mkv`)).status, 404);
  assert.equal((await fetch(`${base}/dl/missing.torrent`)).status, 404);
});

test("the feed answers as a Torznab indexer: its capabilities, and no results for any search", async () => {
  const caps = await (await fetch(`${base}/api?t=caps`)).text();
  assert.match(caps, /<movie-search available="yes"/);
  assert.match(caps, /<category id="2000" name="Movies">/);
  const search = await (await fetch(`${base}/api?t=movie&q=anything`)).text();
  assert.match(search, /<rss version="2.0"/);
  assert.doesNotMatch(search, /<item>/);
  assert.equal((await fetch(`${base}/health`)).status, 200);
});
