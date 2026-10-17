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
 */
export class FakeWorld {
  logRows = [];
  nextCursor = null;
  workflows = [];
  #files = new Map();
  #rows = [];
  #activity = [];
  #jobs = [];
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
      case "/processing/files":
        return { files: this.#rows.filter((row) => row.library_id === Number(query.get("library_id"))) };
      case "/activity/recent":
        return { items: this.#activity.filter((item) => item.event_type === query.get("event_type")) };
      case "/processing/jobs/inspection":
        return { jobs: this.#jobs };
      default:
        throw new Error(`The stand-in has no route ${url.pathname}.`);
    }
  }

  #write(method, path, fields) {
    if (path.endsWith("/watched-folder-remux-scan-dispatch/enqueue")) {
      this.#look(this.workflows.find((workflow) => workflow.id === fields.library_id));
      return { status: 200, json: {}, text: "" };
    }
    const requeue = /^\/processing\/files\/(\d+)\/requeue$/.exec(path);
    if (method === "POST" && requeue) return this.#requeue(Number(requeue[1]));
    throw new Error(`The stand-in has no ${method} route ${path}.`);
  }

  /** Weir looks at the watched folder: every film it finds is held, or cleaned into the output folder. */
  #look(workflow) {
    for (const path of [...this.#files.keys()].filter((name) => name.startsWith(`${workflow.watched_folder}/`) && name.endsWith(".mkv"))) {
      const relative = path.slice(workflow.watched_folder.length + 1);
      if (this.#rows.some((row) => row.library_id === workflow.id && row.relative_path === relative)) continue;
      const row = { id: this.#rows.length + 1, library_id: workflow.id, relative_path: relative, status: "on_hold", output: `${workflow.output_folder}/${relative}` };
      this.#rows.push(row);
      if (!this.#behaviour.neverCleans) this.#clean(row, path);
    }
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
