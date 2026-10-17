// A stand-in for Weir and the container, small enough to read, for testing what the scenarios in CI decide: it answers the routes the scenarios
// use, cleans a film the way Weir does (unless told not to), and can be told to go wrong in the ways the scenarios must catch. The real
// ones are the image under test, in CI.

const ORIGINAL = { video: 1, audio: ["eng", "fre", "jpn"], subtitles: ["eng", "spa"], seconds: 30, bytes: 11_000_000 };
const SKIPPED_ANSWER = { requeued: 0, skipped: 1, detail: "Weir already cleaned this file, so it skipped it. Already done: cleaned on 2026-10-16." };

/**
 * @param {object} [behaviour]
 * @param {(summary: object) => object} [behaviour.cleaning] What Weir makes of the film.
 * @param {boolean} [behaviour.neverCleans] Weir finds the film and never cleans it.
 * @param {boolean} [behaviour.removesOriginal] Weir takes the original away.
 * @param {object} [behaviour.requeue] What Process again answers.
 * @param {boolean} [behaviour.rewritesOnRequeue] Process again writes the output again.
 * @param {string} [behaviour.warnsWhileCleaning] A warning Weir logs when it cleans.
 * @param {boolean} [behaviour.ignoresPauseRequest] Weir never takes the tray's pause request.
 * @param {boolean} [behaviour.keepsRequestFile] Weir applies the tray's request and leaves the file.
 * @param {boolean} [behaviour.cleansWhilePaused] Weir cleans a film although it is paused.
 * @param {boolean} [behaviour.staysPaused] Weir does not resume on the tray's request.
 * @param {boolean} [behaviour.failsVanishedFile] Weir reports a file that was deleted while it waited as a failure.
 * @param {boolean} [behaviour.neverNoticesVanishedFile] Weir never notices that a waiting file is gone.
 */
export class FakeWorld {
  logRows = [];
  nextCursor = null;
  workflows = [];
  #files = new Map();
  #rows = [];
  #activity = [];
  #jobs = [];
  #paused = false;
  #behaviour;
  #now = 0;

  constructor(behaviour = {}) {
    this.#behaviour = behaviour;
  }

  /** A clock that moves only when something sleeps, so a wait that never ends costs no time. */
  clock = {
    now: () => this.#now,
    sleep: async (ms) => {
      this.#now += ms;
    },
  };

  weir = {
    get: async (path) => this.#get(new URL(path, "http://weir")),
    write: async (method, path, fields) => this.#write(method, path, fields),
    createLibrary: async (fields) => {
      const workflow = { id: this.workflows.length + 1, ...fields };
      this.workflows.push(workflow);
      return workflow;
    },
  };

  box = {
    ensureFolders: async () => {},
    writeFileAtomically: async (path, text) => this.#writeFile(path, text),
    writeBytes: async (path, bytes) => void this.#files.set(path, { summary: null, sha: "tiny", modified: "t0", tiny: bytes.length }),
    remove: async (path) => void this.#files.delete(path),
    makeFilm: async ({ path }) => void this.#files.set(path, { summary: ORIGINAL, sha: "master", modified: "t0" }),
    dropRelease: async ({ film, folder, name }) => {
      this.#files.set(`${folder}/${name}.mkv`, { ...this.#files.get(film), modified: "t0" });
      this.#files.set(`${folder}/${name}.nfo`, { summary: null, sha: "nfo", modified: "t0" });
    },
    probe: async (path) => this.#file(path).summary,
    exists: async (path) => this.#files.has(path),
    sha256: async (path) => this.#file(path).sha,
    modified: async (path) => this.#file(path).modified,
  };

  asWorld() {
    return { weir: this.weir, box: this.box, runId: "a1b2c3", clock: this.clock };
  }

  #file(path) {
    const file = this.#files.get(path);
    if (!file) throw new Error(`There is no file ${path}.`);
    return file;
  }

  #get(url) {
    const query = url.searchParams;
    switch (url.pathname) {
      case "/system/log":
        return { items: [...this.logRows], next_cursor: this.nextCursor };
      case "/pause":
        return { paused: this.#paused, scan_while_paused: true };
      case "/processing/files": {
        const files = this.#rows.filter((row) => row.library_id === Number(query.get("library_id")));
        const count = (status) => files.filter((file) => file.status === status).length;
        return { files, status_counts: { processing_failed: count("processing_failed"), rejected: count("rejected") } };
      }
      case "/activity/recent":
        return { items: this.#activity.filter((item) => !query.get("event_type") || item.event_type === query.get("event_type")) };
      case "/processing/jobs/inspection": {
        const statuses = query.getAll("status");
        return { jobs: this.#jobs.filter((job) => statuses.length === 0 || statuses.includes(job.status)) };
      }
      default:
        throw new Error(`The stand-in has no route ${url.pathname}.`);
    }
  }

  #write(method, path, fields) {
    if (path.endsWith("/watched-folder-remux-scan-dispatch/enqueue")) {
      this.#look(this.workflows.find((workflow) => workflow.id === fields.library_id), fields.enqueue_remux_jobs);
      return { status: 200, json: {}, text: "" };
    }
    const requeue = /^\/processing\/files\/(\d+)\/requeue$/.exec(path);
    if (method === "POST" && requeue) return this.#requeue(Number(requeue[1]));
    throw new Error(`The stand-in has no ${method} route ${path}.`);
  }

  /** Weir looks at the watched folder: a film it finds is held, or cleaned into the output folder once nothing holds it. */
  #look(workflow, queueFiles) {
    const watched = [...this.#files.keys()].filter((name) => name.startsWith(`${workflow.watched_folder}/`));
    for (const row of this.#rows.filter((candidate) => candidate.library_id === workflow.id && !watched.includes(candidate.source))) this.#noticeGone(row);
    for (const path of watched.filter((name) => name.endsWith(".mkv"))) {
      const relative = path.slice(workflow.watched_folder.length + 1);
      let row = this.#rows.find((candidate) => candidate.library_id === workflow.id && candidate.relative_path === relative);
      if (!row) {
        row = { id: this.#rows.length + 1, library_id: workflow.id, relative_path: relative, source: path, status: "on_hold", source_gone: false, output: `${workflow.output_folder}/${relative}` };
        this.#rows.push(row);
      }
      if (row.status !== "on_hold") continue;
      if (this.#files.get(path).tiny) row.status_reason = "Waiting for the file to settle.";
      else if (this.#paused && !this.#behaviour.cleansWhilePaused) row.status_reason = "Processing is paused until you resume it.";
      else if (queueFiles && !this.#behaviour.neverCleans) this.#clean(row, path);
    }
    this.#jobs.push({ id: this.#jobs.length + 1, job_kind: "processing.watched_folder.remux_scan_dispatch", status: "completed" });
  }

  #noticeGone(row) {
    if (this.#behaviour.neverNoticesVanishedFile) return;
    row.source_gone = true;
    if (this.#behaviour.failsVanishedFile) Object.assign(row, { status: "processing_failed", failure_class: "source_missing" });
  }

  #writeFile(path, text) {
    if (!path.endsWith("/pause-request.json")) {
      this.#files.set(path, { summary: null, sha: "text", modified: "t0" });
      return;
    }
    if (this.#behaviour.ignoresPauseRequest) {
      this.#files.set(path, { summary: null, sha: "request", modified: "t0" });
      return;
    }
    const { paused } = JSON.parse(text);
    if (!(this.#behaviour.staysPaused && !paused)) this.#paused = paused;
    this.#activity.push({ event_type: paused ? "system.processing_paused" : "system.processing_resumed", title: paused ? "Paused" : "Resumed", detail: "The tray", result: "completed" });
    if (this.#behaviour.keepsRequestFile) this.#files.set(path, { summary: null, sha: "request", modified: "t0" });
  }

  #clean(row, source) {
    const cleaning = this.#behaviour.cleaning ?? ((summary) => ({ ...summary, audio: ["eng"], subtitles: [], bytes: 6_000_000 }));
    this.#files.set(row.output, { summary: cleaning(this.#file(source).summary), sha: "cleaned", modified: "t1" });
    row.status = "processed";
    this.#jobs.push({ id: this.#jobs.length + 1, job_kind: "processing.file.remux_pass", status: "completed" });
    const name = row.relative_path.split("/").at(-1);
    this.#activity.push({ event_type: "processing.file_remux_pass_completed", library_id: row.library_id, title: `Cleaned ${name}`, result: "completed" });
    if (this.#behaviour.removesOriginal) this.#files.delete(source);
    if (this.#behaviour.warnsWhileCleaning) this.logRows.push({ source: "server", id: 1, at: "2026-10-16T10:00:00Z", title: this.#behaviour.warnsWhileCleaning });
  }

  #requeue(id) {
    const row = this.#rows.find((candidate) => candidate.id === id);
    const answer = this.#behaviour.requeue ?? SKIPPED_ANSWER;
    if (answer.skipped > 0) {
      const name = row.relative_path.split("/").at(-1);
      this.#activity.push({ event_type: "processing.file_skipped_repeat", library_id: row.library_id, trigger: "manual", title: `Skipped: already done (${name})`, result: "skipped" });
    }
    if (answer.requeued > 0) this.#jobs.push({ id: this.#jobs.length + 1, job_kind: "processing.file.remux_pass", status: "pending" });
    if (this.#behaviour.rewritesOnRequeue) this.#file(row.output).modified = "t2";
    return { status: 200, json: answer, text: "" };
  }
}
