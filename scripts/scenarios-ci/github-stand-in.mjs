// A stand-in for the two GitHub addresses the tray updates from, serving a release feed made from the package this commit built: the public
// release feed (`releases.atom`), and each release's download folder (`releases.win.json` and the packages). The files are the ones vpk packed,
// so the tray's own check, download, verification and apply run against real Velopack output. What differs from GitHub is only where the
// requests land (the PC's hosts file points github.com here) and the faults it can be told to show.
import { readFile } from "node:fs/promises";
import { createServer as createHttpServer } from "node:http";
import { createServer as createHttpsServer } from "node:https";
import { join } from "node:path";

export const REPOSITORY = "jampat000/Weir";
const DOWNLOADS = new RegExp(`^/${REPOSITORY}/releases/download/([^/]+)/([^/]+)$`, "i");

/**
 * What the feed shows:
 * - `newer`: the build under test is published, with the delta against the release the machine runs and the full package.
 * - `newer-full`: the same, with no delta, so a client has only the full package to take.
 * - `current`: only the release the machine runs is published, so there is nothing newer.
 * - `corrupt`: the build under test is published, and every package is served with one byte changed after its hash was recorded.
 * - `down`: everything answers 503.
 */
export const MODES = ["newer", "newer-full", "current", "corrupt", "down"];

const escapeXml = (text) => text.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll('"', "&quot;");

/** The release feed as GitHub serves it: one entry per release, newest first, each with the address of its page. */
export function atomFeed(tags, { updated }) {
  const entries = tags.map(
    (tag) => `  <entry>
    <id>tag:github.com,2008:Repository/1/${escapeXml(tag)}</id>
    <updated>${updated}</updated>
    <link rel="alternate" type="text/html" href="https://github.com/${REPOSITORY}/releases/tag/${escapeXml(tag)}"/>
    <title>${escapeXml(tag)}</title>
  </entry>`,
  );
  return `<?xml version="1.0" encoding="UTF-8"?>
<feed xmlns="http://www.w3.org/2005/Atom" xml:lang="en-US">
  <id>tag:github.com,2008:https://github.com/${REPOSITORY}/releases</id>
  <link type="text/html" rel="alternate" href="https://github.com/${REPOSITORY}/releases"/>
  <link type="application/atom+xml" rel="self" href="https://github.com/${REPOSITORY}/releases.atom"/>
  <title>Release notes from Weir</title>
  <updated>${updated}</updated>
${entries.join("\n")}
</feed>
`;
}

/** The Velopack feed of a release without its delta packages. */
export function withoutDeltas(releasesJson) {
  const feed = JSON.parse(releasesJson);
  return JSON.stringify({ ...feed, Assets: feed.Assets.filter((asset) => asset.Type !== "Delta") });
}

/** The bytes with the middle one changed. */
export function withOneByteChanged(bytes) {
  const changed = Buffer.from(bytes);
  const at = Math.floor(changed.length / 2);
  changed[at] ^= 0xff;
  return changed;
}

export class GithubStandIn {
  #folder;
  #current;
  #previous;
  #updated;
  #mode = "newer";
  #server = null;

  /** Every request the stand-in answered, oldest first: `{ host, path, status }`. */
  requests = [];

  /**
   * @param {object} release
   * @param {string} release.folder Where vpk packed the build under test: `releases.win.json` and its packages.
   * @param {{tag: string, releasesJson: string}} release.current The build under test, as a release tag.
   * @param {{tag: string, releasesJson: string}} release.previous The release the machine runs: its tag and the `releases.win.json` it published.
   * @param {string} release.updated When the releases are said to have been updated, as GitHub writes it.
   */
  constructor({ folder, current, previous, updated }) {
    this.#folder = folder;
    this.#current = current;
    this.#previous = previous;
    this.#updated = updated;
  }

  get mode() {
    return this.#mode;
  }

  /** Chooses what the feed shows from now on. */
  use(mode) {
    if (!MODES.includes(mode)) throw new Error(`The feed has no mode "${mode}"; it has ${MODES.join(", ")}.`);
    this.#mode = mode;
  }

  /** The packages asked for since `mark` (the length of `requests` then): the file names, in the order they were asked for. */
  packagesAskedFor(mark = 0) {
    return this.requests
      .slice(mark)
      .filter((request) => request.status === 200 && request.path.endsWith(".nupkg"))
      .map((request) => request.path.slice(request.path.lastIndexOf("/") + 1));
  }

  /** @param {{tls?: {pfx: Buffer, passphrase: string}, port: number, host?: string}} options */
  listen({ tls, port, host = "127.0.0.1" }) {
    const handler = (request, response) => {
      this.#answer(request).then(
        (answer) => this.#send(request, response, answer),
        (error) => this.#send(request, response, { status: 500, type: "text/plain", body: error.message }),
      );
    };
    const server = tls ? createHttpsServer({ pfx: tls.pfx, passphrase: tls.passphrase }, handler) : createHttpServer(handler);
    this.#server = server;
    return new Promise((resolve, reject) => {
      server.once("error", reject);
      server.listen(port, host, () => resolve(server.address().port));
    });
  }

  close() {
    return new Promise((resolve) => {
      if (!this.#server) return resolve();
      this.#server.closeAllConnections();
      this.#server.close(() => resolve());
    });
  }

  #send(request, response, answer) {
    this.requests.push({ host: request.headers.host ?? "", path: new URL(request.url, "http://stand-in").pathname, status: answer.status });
    response.writeHead(answer.status, { "Content-Type": answer.type, "Content-Length": answer.body.length, Connection: "close" });
    response.end(request.method === "HEAD" ? undefined : answer.body);
  }

  async #answer(request) {
    const host = (request.headers.host ?? "").split(":")[0].toLowerCase();
    const path = new URL(request.url, "http://stand-in").pathname;
    if (this.#mode === "down") return text(503, "GitHub is down.");
    if (host === "api.github.com") return text(404, "Not found.");

    if (path.toLowerCase() === `/${REPOSITORY}/releases.atom`.toLowerCase()) {
      const tags = this.#mode === "current" ? [this.#previous.tag] : [this.#current.tag, this.#previous.tag];
      return { status: 200, type: "application/atom+xml; charset=utf-8", body: Buffer.from(atomFeed(tags, { updated: this.#updated })) };
    }

    const download = DOWNLOADS.exec(path);
    if (!download) return text(404, "Not found.");
    const [, tag, file] = download.map((part) => decodeURIComponent(part));
    if (tag === this.#previous.tag) return file === "releases.win.json" ? json(this.#previous.releasesJson) : text(404, "Not found.");
    if (tag !== this.#current.tag || this.#mode === "current") return text(404, "Not found.");

    if (file === "releases.win.json") return json(this.#mode === "newer-full" ? withoutDeltas(this.#current.releasesJson) : this.#current.releasesJson);
    const listed = JSON.parse(this.#current.releasesJson).Assets.find((asset) => asset.FileName === file);
    if (!listed || (this.#mode === "newer-full" && listed.Type === "Delta")) return text(404, "Not found.");
    const bytes = await readFile(join(this.#folder, file));
    return { status: 200, type: "application/octet-stream", body: this.#mode === "corrupt" ? withOneByteChanged(bytes) : bytes };
  }
}

const text = (status, body) => ({ status, type: "text/plain; charset=utf-8", body: Buffer.from(body) });
const json = (body) => ({ status: 200, type: "application/json", body: Buffer.from(body) });
