// A client for the parts of Weir's API the smoke uses, sending what the web app sends: the session cookie, a CSRF token in the body, the
// X-Requested-With header and the page's origin.

const API = "/api/v1";

/** The `name=value` of the session cookie in a response's Set-Cookie headers, or null when it set none. */
export function sessionCookie(setCookieHeaders) {
  const first = setCookieHeaders[0];
  return first ? first.split(";")[0] : null;
}

export class WeirClient {
  #origin;
  #cookie = null;

  constructor(origin) {
    this.#origin = origin;
  }

  /** Forgets the session, as a browser that has closed does. */
  forgetSession() {
    this.#cookie = null;
  }

  async #send(method, path, body) {
    // A connection of its own for each request: one kept alive across a restart of the stack is dead, and fetch reports that as a failure.
    const headers = { Accept: "application/json", Connection: "close", Origin: this.#origin, "X-Requested-With": "XMLHttpRequest" };
    if (this.#cookie) headers.Cookie = this.#cookie;
    if (body !== undefined) headers["Content-Type"] = "application/json";
    const response = await fetch(this.#origin + API + path, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: AbortSignal.timeout(30_000),
    }).catch((error) => {
      throw new Error(`${method} ${path} did not get an answer: ${error.cause?.code ?? error.cause?.message ?? error.message}`, { cause: error });
    });
    const text = await response.text();
    let json = null;
    try {
      json = text ? JSON.parse(text) : null;
    } catch {
      json = null;
    }
    return { status: response.status, json, text, cookie: sessionCookie(response.headers.getSetCookie()) };
  }

  async #expect(response, status, what) {
    if (response.status !== status) {
      throw new Error(`${what}: expected HTTP ${status}, got ${response.status}: ${response.text.slice(0, 300)}`);
    }
    return response.json;
  }

  async #csrf() {
    return (await this.#expect(await this.#send("GET", "/auth/csrf"), 200, "csrf token")).csrf_token;
  }

  async #read(path, what) {
    return this.#expect(await this.#send("GET", path), 200, what);
  }

  async #write(method, path, fields, status, what) {
    return this.#expect(await this.#send(method, path, { ...fields, csrf_token: await this.#csrf() }), status, what);
  }

  bootstrapStatus() {
    return this.#read("/auth/bootstrap/status", "bootstrap status");
  }

  async bootstrap(username, password, setupCode) {
    const fields = { username, password, csrf_token: await this.#csrf() };
    if (setupCode) fields.setup_code = setupCode;
    const response = await this.#send("POST", "/auth/bootstrap", fields);
    await this.#expect(response, 200, "creating the first account");
    this.#cookie = response.cookie;
  }

  async login(username, password) {
    const response = await this.#send("POST", "/auth/login", { username, password, csrf_token: await this.#csrf() });
    await this.#expect(response, 200, "signing in");
    this.#cookie = response.cookie;
  }

  /** The status Weir answers a sign-in with, for a password it is expected to refuse; a session is kept only when it signs in. */
  async loginStatus(username, password) {
    const response = await this.#send("POST", "/auth/login", { username, password, csrf_token: await this.#csrf() });
    if (response.status === 200) this.#cookie = response.cookie;
    return response.status;
  }

  /** The sessions signed in to the account, as Weir lists them to the one asking. */
  async activeSessions() {
    return (await this.#read("/auth/sessions", "listing the signed-in sessions")).items;
  }

  /** Activity entries of one event type, newest first. */
  async activity(eventType) {
    return (await this.#read(`/activity/recent?event_type=${eventType}&limit=20`, "reading Activity")).items;
  }

  /** The signed-in account's user name, or null when the session is not accepted. */
  async signedInAs() {
    const response = await this.#send("GET", "/auth/me");
    return response.status === 200 ? response.json.user.username : null;
  }

  createLibrary(fields) {
    return this.#write("POST", "/processing/libraries", fields, 201, "creating a workflow");
  }

  /** Saves new fields over a workflow; `fields` is the whole of what the workflow screen sends. */
  updateLibrary(id, fields) {
    return this.#write("PUT", `/processing/libraries/${id}`, fields, 200, "saving a workflow");
  }

  libraries() {
    return this.#read("/processing/libraries", "listing workflows");
  }

  /** Asks for an unchanged pass-through of a file in a movie workflow's watched folder; returns the job's id. */
  async enqueuePassThrough(relativeMediaPath) {
    const queued = await this.#write(
      "POST",
      "/processing/jobs/file-remux-pass/enqueue",
      { relative_media_path: relativeMediaPath, media_scope: "movie", pass_through_unchanged: true },
      200,
      "queueing a pass-through",
    );
    return queued.job_id;
  }

  /** The job as the processing inspection lists it (id, status, last_error), or undefined when it is not listed. */
  async job(id) {
    const inspection = await this.#read("/processing/jobs/inspection?limit=100", "inspecting the jobs");
    return inspection.jobs.find((job) => job.id === id);
  }

  suiteSettings() {
    return this.#read("/suite/settings", "reading the saved settings");
  }

  saveSuiteSettings(fields) {
    return this.#write("PUT", "/suite/settings", fields, 200, "saving a setting");
  }

  overview() {
    return this.#read("/system/overview", "reading the system overview");
  }
}
