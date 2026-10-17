// Deluno, qBittorrent and Weir set up the way a person sets them up, through each application's own API: the first accounts, the download
// client, the movies library, then the two applications connected to each other. Deluno is used only through its published image and its public
// routes. The folders are the same path in every container that uses them (with-deluno/compose.yaml).
import assert from "node:assert/strict";
import { pbkdf2Sync, randomBytes } from "node:crypto";

import { pollUntil } from "../../docker-smoke/poll.mjs";
import { DelunoApi } from "./deluno-api.mjs";
import { send } from "./http.mjs";
import { QbittorrentApi } from "./qbittorrent-api.mjs";

/** Where the stack's applications answer on this machine (with-deluno/compose.yaml publishes these ports). */
export const DELUNO_ORIGIN = "http://localhost:5099";
export const QBITTORRENT_ORIGIN = "http://localhost:18081";
const FIXTURE_ORIGIN = "http://localhost:18765";

/** Where the applications reach one another, on the stack's own network. */
const DELUNO_INSIDE = "http://deluno:8080";
const WEIR_INSIDE = "http://weir:9347";
const FIXTURE_INSIDE = "http://fixture:8765";

export const FOLDERS = { downloads: "/downloads/completed/Movies", library: "/media/Movies", weirOutput: "/weir/Ready/Movies" };
export const MOVIES_CATEGORY = "deluno-movies";
/** The folder every container sees the release source's films in: the scenario makes a film here, in Weir's container, and the source offers it. */
export const FILM_FOLDER = "/work/source";
// Deluno treats a film shorter than two minutes as a likely sample.
export const FILM_SECONDS = 130;
const READY_CEILING_MS = 180_000;
// qBittorrent keeps a password as PBKDF2-HMAC-SHA512 over 100,000 rounds, written as salt:hash in base64.
const PBKDF2_ROUNDS = 100_000;
const PBKDF2_BYTES = 64;

/** The password of a login, in the form qBittorrent's settings file holds it. */
export function qbittorrentPasswordHash(password, salt) {
  return `${salt.toString("base64")}:${pbkdf2Sync(password, salt, PBKDF2_ROUNDS, PBKDF2_BYTES, "sha512").toString("base64")}`;
}

/** A login for this run's qBittorrent, made fresh; the password is known only to this run. */
export function newLogins() {
  const qbittorrent = { username: "scenarios", password: randomBytes(18).toString("hex") };
  return {
    qbittorrent: { ...qbittorrent, pbkdf2: qbittorrentPasswordHash(qbittorrent.password, randomBytes(16)) },
    deluno: { username: "scenarios", displayName: "Weir scenarios", password: `Pw-${randomBytes(18).toString("hex")}-Aa1` },
  };
}

export class DelunoWorld {
  deluno;
  qbittorrent;
  #weir;
  #library;
  #indexer;
  #connection = null;

  constructor({ deluno, qbittorrent, weir, library, indexer }) {
    this.deluno = deluno;
    this.#indexer = indexer;
    this.qbittorrent = qbittorrent;
    this.#weir = weir;
    this.#library = library;
  }

  /** Waits for Deluno and qBittorrent to answer, then sets both up: the accounts, the client, a quality profile and the movies library. */
  static async setUp({ weir, logins, clock }) {
    const ready = (origin, path, what) => pollUntil(async () => ((await send(origin, { method: "GET", path, what }).catch(() => ({ status: 0 }))).status === 200 ? true : undefined), { what, ceilingMs: READY_CEILING_MS, intervalMs: 2000, clock });
    await ready(DELUNO_ORIGIN, "/health", "Deluno to answer");
    await ready(QBITTORRENT_ORIGIN, "/", "qBittorrent to answer");
    await ready(FIXTURE_ORIGIN, "/health", "the release source to answer");

    const deluno = new DelunoApi(DELUNO_ORIGIN);
    await deluno.createFirstAccount(logins.deluno);

    const qbittorrent = await QbittorrentApi.signIn(QBITTORRENT_ORIGIN, logins.qbittorrent);
    await qbittorrent.setPreferences({ save_path: "/downloads/completed", temp_path_enabled: true, temp_path: "/downloads/incomplete", auto_tmm_enabled: true, dht: false, pex: false, lsd: false });
    await qbittorrent.createCategory(MOVIES_CATEGORY, FOLDERS.downloads);

    const client = await deluno.must("POST", "/api/download-clients", {
      name: "qBittorrent",
      username: logins.qbittorrent.username,
      password: logins.qbittorrent.password,
      protocol: "qbittorrent",
      host: "qbittorrent",
      port: 8080,
      moviesCategory: MOVIES_CATEGORY,
      tvCategory: "deluno-tv",
      priority: 1,
      isEnabled: true,
    });
    // Deluno sends only to a client whose connection test passed, so test it as a person does.
    const test = await deluno.call("POST", `/api/download-clients/${client.id}/test`);
    assert.ok(test.status >= 200 && test.status < 300, `Deluno's test of qBittorrent failed: ${test.status} ${test.text.slice(0, 400)}`);

    const profile = await deluno.must("POST", "/api/quality-profiles", {
      name: "Balanced 1080p",
      mediaType: "movies",
      cutoffQuality: "WEB 1080p",
      allowedQualities: "WEB 720p,Bluray 720p,WEB 1080p,Bluray 1080p",
      customFormatIds: "",
      upgradeUntilCutoff: true,
      upgradeUnknownItems: false,
    });
    const library = await deluno.must("POST", "/api/libraries", {
      name: "Movies",
      mediaType: "movies",
      purpose: "Main library",
      rootPath: FOLDERS.library,
      downloadsPath: FOLDERS.downloads,
      qualityProfileId: profile.id,
      autoSearchEnabled: false,
      missingSearchEnabled: false,
      upgradeSearchEnabled: false,
      removeEmptySourceFolders: false,
      searchIntervalMinutes: 720,
      retryDelayHours: 6,
      maxItemsPerRun: 10,
    });

    // The release source is a Torznab feed, added as an indexer as a person adds one. Nothing searches it: the scenario sends its release itself.
    const indexer = await deluno.must("POST", "/api/indexers", {
      name: "Scenario feed",
      protocol: "torznab",
      privacy: "public",
      baseUrl: FIXTURE_INSIDE,
      apiKey: "scenario",
      priority: 25,
      categories: "2000,2040,5000,5040",
      mediaScope: "both",
      isEnabled: true,
      keepName: true,
      routeToLibraries: true,
      rssEnabled: false,
      automaticSearchEnabled: false,
    });
    const indexerTest = await deluno.call("POST", `/api/indexers/${indexer.id}/test`);
    assert.ok(indexerTest.status >= 200 && indexerTest.status < 300, `Deluno's test of the release source failed: ${indexerTest.status} ${indexerTest.text.slice(0, 400)}`);
    await deluno.must("PUT", `/api/libraries/${library.id}/routing`, {
      sources: [{ indexerId: indexer.id, priority: 1, requiredTags: "", excludedTags: "" }],
      downloadClients: [{ downloadClientId: client.id, priority: 1 }],
    });
    // The planner that hands a finished download to Weir is one of the automatic jobs.
    await deluno.must("PUT", "/api/settings/automation", { isEnabled: true });
    return new DelunoWorld({ deluno, qbittorrent, weir, library, indexer });
  }

  /**
   * Makes a film in Weir's container, as the scenario's film, and has the release source make a release of it that qBittorrent can download:
   * the film and an .nfo in a folder named like a release, and a torrent that names the source as its web seed.
   * @returns {Promise<{title: string, year: number, release: string, infohash: string, size: number, url: string}>}
   */
  async makeFilm({ box, title, year }) {
    const source = `${FILM_FOLDER}/${title}.mkv`;
    await box.makeFilm({ path: source, seconds: FILM_SECONDS });
    const made = await send(FIXTURE_ORIGIN, { method: "POST", path: "/releases", body: { source, title, year }, what: "The release source" });
    assert.equal(made.status, 200, `The release source made the release (answered ${made.status}: ${made.text.slice(0, 300)})`);
    return made.json;
  }

  /** Adds the film to Deluno and sends its release to qBittorrent, as "Send anyway" on the title's page does. */
  async sendFilm(film) {
    const movie = await this.deluno.must("POST", "/api/movies", { title: film.title, releaseYear: film.year, imdbId: null, monitored: true });
    await this.deluno.must("POST", `/api/movies/${movie.id}/grab`, {
      releaseName: film.release,
      indexerId: this.#indexer.id,
      indexerName: this.#indexer.name,
      candidateQuality: "WEB 1080p",
      downloadUrl: film.url,
      sizeBytes: film.size,
      seeders: 50,
      force: true,
      overrideReason: "Weir scenarios",
      duplicateAction: null,
    });
    return movie;
  }

  /** The newest hand-off Deluno has for a release, or undefined. */
  async handoffOf(release) {
    const all = await this.deluno.handoffs();
    return all.filter((handoff) => String(handoff.releaseName).includes(release)).sort((a, b) => String(b.createdUtc).localeCompare(String(a.createdUtc)))[0];
  }

  /**
   * Connects Weir and Deluno to each other, as a person does: Deluno's key given to Weir as a media manager, Weir's webhook secret given to Deluno
   * as the processor of the movies library, which then refines before it imports. Done once; later calls return the same connection.
   * @returns {Promise<{id: number, how: string}>} The connection as Weir has it.
   */
  async connectWeir() {
    this.#connection ??= this.#connect();
    return this.#connection;
  }

  async #connect() {
    const key = await this.deluno.makeApiKey("Weir", "read,imports");
    const made = await this.#weir.write("POST", "/media-managers/connections", { kind: "deluno", base_url: DELUNO_INSIDE, api_key: key, enabled: true });
    assert.ok(made.status === 200 || made.status === 201, `Weir accepted the connection to Deluno (answered ${made.status}: ${made.text})`);
    const connectionId = made.json.id;
    const secret = await this.#weir.write("POST", `/media-managers/connections/${connectionId}/webhook-secret`);
    assert.equal(secret.status, 200, `Weir made a webhook secret for Deluno (answered ${secret.status}: ${secret.text})`);

    const processor = await this.deluno.must("POST", "/api/integrations/processors/connections", {
      name: "Weir",
      provider: "generic-webhook",
      submissionUrl: `${WEIR_INSIDE}${secret.json.webhook_url_path}`,
      authHeaderName: secret.json.header_name ?? "X-Webhook-Secret",
      secret: secret.json.webhook_secret,
      isEnabled: true,
      healthCheckUrl: `${WEIR_INSIDE}/ready`,
    });
    await this.deluno.must("PUT", `/api/libraries/${this.#library.id}/workflow`, {
      importWorkflow: "refine-before-import",
      processorConnectionId: processor.id,
      processorName: processor.name,
      processorOutputPath: FOLDERS.weirOutput,
      processorTimeoutMinutes: 2880,
      processorFailureMode: "block",
      cleanupMode: "keep-source",
      removeEmptySourceFolders: false,
    });
    return { id: connectionId, how: "connected Weir to Deluno with an API key made in Deluno, and Deluno to Weir with Weir's webhook secret" };
  }
}
