// The release source of the with-deluno shard, for a Deluno that has no internet: it makes a release of a film the scenario made, offers it as a
// torrent, serves its bytes as the torrent's web seed, and answers as the Torznab feed Deluno is given as an indexer. Nothing here is a product:
// Deluno, qBittorrent and Weir are the real published images.
//
//   POST /releases {"source": "/work/source/x.mkv", "title": "Name", "year": 2020}   makes the release folder (the film and an .nfo),
//                                                                                  its torrent, and answers {release, infohash, size, url, files}
//        optional: "zeros": true      the release's film is as long as the source but all zero bytes: a file nothing can read
//                  "extras": [{"path": "Extras/Featurette.mkv", "bytes": 2000000}]   more files in the folder, each all zero bytes
//   GET  /dl/<release>.torrent                                                      the torrent
//   GET  /webseed/<release>/<file>                                                  the release's bytes, with ranges
//   GET  /api?t=caps|search|movie|tvsearch                                          a Torznab feed that offers nothing: the scenario sends its release itself
//   GET  /health
import fs from "node:fs";
import http from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { makeTorrent } from "./torrent.mjs";

const CAPS = `<?xml version="1.0" encoding="UTF-8"?>
<caps>
  <server title="Weir scenario feed"/>
  <limits max="100" default="50"/>
  <searching>
    <search available="yes" supportedParams="q"/>
    <movie-search available="yes" supportedParams="q,imdbid"/>
    <tv-search available="yes" supportedParams="q,season,ep"/>
  </searching>
  <categories>
    <category id="2000" name="Movies"><subcat id="2040" name="Movies/HD"/></category>
    <category id="5000" name="TV"><subcat id="5040" name="TV/HD"/></category>
  </categories>
</caps>
`;
const EMPTY_RESULTS = `<?xml version="1.0" encoding="UTF-8"?>
<rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel><title>Weir scenario feed</title></channel></rss>
`;

const reply = (response, status, type, body) => {
  response.writeHead(status, { "Content-Type": type });
  response.end(body);
};

/** The bytes a Range header asks of a file of this size: `{start, end}` (inclusive), null for no range, or "unsatisfiable". */
export function parseRange(header, size) {
  const match = /^bytes=(\d*)-(\d*)$/.exec(header ?? "");
  if (!match || (match[1] === "" && match[2] === "")) return null;
  const start = match[1] === "" ? Math.max(size - Number(match[2]), 0) : Number(match[1]);
  const end = match[1] === "" || match[2] === "" ? size - 1 : Math.min(Number(match[2]), size - 1);
  return start > end || start >= size ? "unsatisfiable" : { start, end };
}

function serveFile(request, response, file) {
  const size = fs.statSync(file).size;
  const range = parseRange(request.headers.range, size);
  if (range === "unsatisfiable") {
    response.writeHead(416, { "Content-Range": `bytes */${size}` });
    return response.end();
  }
  const headers = { "Content-Type": "application/octet-stream", "Accept-Ranges": "bytes" };
  if (range) {
    response.writeHead(206, { ...headers, "Content-Length": range.end - range.start + 1, "Content-Range": `bytes ${range.start}-${range.end}/${size}` });
    return fs.createReadStream(file, range).pipe(response);
  }
  response.writeHead(200, { ...headers, "Content-Length": size });
  return fs.createReadStream(file).pipe(response);
}

const MAX_EXTRA_BYTES = 50 * 1024 * 1024;
/** An extra is a file below the release folder, named in plain words, of a size a scenario can afford. */
export const validExtra = (extra) =>
  typeof extra?.path === "string" &&
  /^[A-Za-z0-9._-]+(\/[A-Za-z0-9._-]+)*$/.test(extra.path) &&
  !extra.path.split("/").some((part) => part === "." || part === "..") &&
  Number.isInteger(extra.bytes) &&
  extra.bytes >= 1 &&
  extra.bytes <= MAX_EXTRA_BYTES;

/** @param {{work: string, publicBase: string}} options `work` holds the films the scenario made and the releases made from them. */
export function createFixture({ work, publicBase }) {
  const releases = path.join(work, "releases");
  const inside = (folder, file) => file.startsWith(`${path.resolve(folder)}${path.sep}`);
  const torrents = new Map();

  function makeRelease(response, body) {
    const { source, title, year, zeros = false, extras = [] } = JSON.parse(body);
    if (!/^[A-Za-z0-9]+$/.test(title ?? "") || !Number.isInteger(year)) return reply(response, 400, "text/plain", "title must be letters and digits, and year a number");
    if (!Array.isArray(extras) || !extras.every(validExtra)) return reply(response, 400, "text/plain", "each extra needs a plain relative path and a size between 1 byte and 50 MB");
    const film = path.resolve(source ?? "");
    if (!inside(work, film) || !fs.existsSync(film)) return reply(response, 400, "text/plain", `no film at ${source}`);
    const release = `${title}.${year}.1080p.WEB-DL.x264-WEIR`;
    const folder = path.join(releases, release);
    if (fs.existsSync(folder)) return reply(response, 409, "text/plain", "a release of that name already exists");
    fs.mkdirSync(folder, { recursive: true });
    if (zeros) fs.writeFileSync(path.join(folder, `${release}.mkv`), Buffer.alloc(fs.statSync(film).size));
    else fs.copyFileSync(film, path.join(folder, `${release}.mkv`));
    for (const extra of extras) {
      const file = path.join(folder, ...extra.path.split("/"));
      fs.mkdirSync(path.dirname(file), { recursive: true });
      fs.writeFileSync(file, Buffer.alloc(extra.bytes));
    }
    fs.writeFileSync(path.join(folder, `${release}.nfo`), "Weir scenario release\n");
    const made = makeTorrent({ folder, name: release, webSeedBase: `${publicBase}/webseed/` });
    torrents.set(release, made.torrent);
    console.log(`made ${release}: ${made.total} bytes in ${made.files.length} files, hash ${made.infohash}`);
    return reply(response, 200, "application/json", JSON.stringify({ title, year, release, infohash: made.infohash, size: made.total, url: `${publicBase}/dl/${release}.torrent`, files: made.files }));
  }

  return http.createServer((request, response) => {
    const url = new URL(request.url, "http://fixture");
    if (request.method === "POST" && url.pathname === "/releases") {
      let body = "";
      request.on("data", (chunk) => (body += chunk));
      request.on("end", () => {
        try {
          makeRelease(response, body);
        } catch (error) {
          reply(response, 500, "text/plain", String(error.message));
        }
      });
      return;
    }
    if (url.pathname === "/health") return reply(response, 200, "text/plain", "ok");
    if (url.pathname === "/api") return reply(response, 200, "application/xml", url.searchParams.get("t") === "caps" ? CAPS : EMPTY_RESULTS);
    const torrent = /^\/dl\/(.+)\.torrent$/.exec(url.pathname);
    if (torrent && torrents.has(torrent[1])) return reply(response, 200, "application/x-bittorrent", torrents.get(torrent[1]));
    if (url.pathname.startsWith("/webseed/")) {
      const file = path.resolve(releases, decodeURIComponent(url.pathname.slice("/webseed/".length)));
      if (inside(releases, file) && fs.existsSync(file) && fs.statSync(file).isFile()) return serveFile(request, response, file);
    }
    return reply(response, 404, "text/plain", "not here");
  });
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const port = Number(process.env.FIXTURE_PORT ?? 8765);
  createFixture({ work: process.env.FIXTURE_WORK ?? "/work", publicBase: process.env.FIXTURE_PUBLIC_BASE ?? `http://fixture:${port}` }).listen(port, () => console.log(`fixture listening on ${port}`));
}
