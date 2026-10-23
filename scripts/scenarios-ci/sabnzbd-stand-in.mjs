// A stand-in for SABnzbd, enough for Weir's connection test: it answers the settings read (`mode=get_config&section=misc`) when the API key is
// the one it was given, and refuses any other with 401, as Weir's test reads a refused key. A scenario saves a connection to it with a key,
// updates Weir, and tests the connection again: the stand-in only answers if the key Weir sends is the one that was saved, so Weir has
// to read its stored key back with the credentials secret. The shape is the one the contract suite's own download-client stand-in uses
// (Weir.Contract.Tests/Harness/Fakes/FakeDownloadClient.cs); it is not a recording of SABnzbd.
import { createServer } from "node:http";

export class SabnzbdStandIn {
  #apiKey;
  #server = null;

  /** The API key of every request, oldest first, and whether it was the right one. */
  asked = [];

  constructor(apiKey) {
    this.#apiKey = apiKey;
  }

  /** Starts listening on a free port (every interface, so `localhost` finds it) and returns the port. */
  listen() {
    this.#server = createServer((request, response) => {
      const query = new URL(request.url, "http://stand-in").searchParams;
      const right = query.get("apikey") === this.#apiKey;
      this.asked.push({ mode: query.get("mode"), section: query.get("section"), right });
      const answered = right && request.url.startsWith("/api") && query.get("mode") === "get_config" && query.get("section") === "misc";
      const status = right ? (answered ? 200 : 404) : 401;
      const body = answered ? { config: { misc: { complete_dir: "C:\\Downloads\\complete" } } } : { status: false, error: right ? "Not found" : "API Key Incorrect" };
      response.writeHead(status, { "Content-Type": "application/json", Connection: "close" });
      response.end(JSON.stringify(body));
    });
    return new Promise((resolve, reject) => {
      this.#server.once("error", reject);
      this.#server.listen(0, "127.0.0.1", () => resolve(this.#server.address().port));
    });
  }

  close() {
    return new Promise((resolve) => {
      if (!this.#server) return resolve();
      this.#server.closeAllConnections();
      this.#server.close(() => resolve());
    });
  }
}
