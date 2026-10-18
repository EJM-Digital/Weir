// Deluno's own API, as its public routes take it: a person's account and bearer token for setting Deluno up, and its hand-off list. Deluno is
// used only through its published image and these routes; nothing of Deluno's source is here.
import { send } from "./http.mjs";

export class DelunoApi {
  #origin;
  #token = null;

  constructor(origin) {
    this.#origin = origin;
  }

  #request(method, path, body) {
    const headers = this.#token ? { Authorization: `Bearer ${this.#token}` } : {};
    return send(this.#origin, { method, path, headers, body, what: "Deluno" });
  }

  /** Sends a request and returns the answer whatever its status. */
  call(method, path, body) {
    return this.#request(method, path, body);
  }

  /** Sends a request that must work and returns what it answered; any other status throws with the request and what Deluno said. */
  async must(method, path, body) {
    const answer = await this.#request(method, path, body);
    if (answer.status < 200 || answer.status >= 300) throw new Error(`Deluno ${method} ${path} answered ${answer.status}: ${answer.text.slice(0, 600)}`);
    return answer.json;
  }

  /** Makes the first account, as a person does on a new Deluno, and signs in as it. */
  async createFirstAccount({ username, displayName, password }) {
    const status = await this.must("GET", "/api/auth/bootstrap-status");
    if (!status.requiresSetup) throw new Error("A new Deluno must offer to create the first account.");
    await this.must("POST", "/api/auth/bootstrap", { username, displayName, password });
    this.#token = (await this.must("POST", "/api/auth/login", { username, password })).accessToken;
  }

  /** An API key with these scopes, for another application to call Deluno with. */
  async makeApiKey(name, scopes) {
    return (await this.must("POST", "/api/api-keys", { name, scopes })).apiKey;
  }

  /** Deluno's hand-offs to its processors, newest first, as it reports them. */
  handoffs() {
    return this.must("GET", "/api/integrations/processors/handoffs?take=200");
  }
}
