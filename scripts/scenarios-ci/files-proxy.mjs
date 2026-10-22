// A TCP relay that can stop relaying without closing anything: connections stay open, nothing more is passed in either direction. Put between
// Windows' SMB client and its own SMB server it makes a share that hangs, the way a NAS that has stopped answering does, which a share that is
// removed or a service that is stopped does not (those are refused at once).
import { createConnection, createServer } from "node:net";

export class FreezableRelay {
  #server;
  #pairs = new Set();
  #frozen = false;

  /** Starts relaying from a port of this PC's loopback to `target`. Resolves to the relay, listening. */
  static async start({ target }) {
    const relay = new FreezableRelay(target);
    await new Promise((resolve, reject) => {
      relay.#server.once("error", reject);
      relay.#server.listen(0, "127.0.0.1", resolve);
    });
    return relay;
  }

  constructor(target) {
    this.target = target;
    this.#server = createServer((client) => this.#relay(client));
  }

  get port() {
    return this.#server.address().port;
  }

  get frozen() {
    return this.#frozen;
  }

  #relay(client) {
    const upstream = createConnection({ host: this.target.host, port: this.target.port });
    const pair = { client, upstream };
    this.#pairs.add(pair);
    const done = () => {
      client.destroy();
      upstream.destroy();
      this.#pairs.delete(pair);
    };
    for (const socket of [client, upstream]) {
      socket.on("error", done);
      socket.on("close", done);
    }
    // A frozen relay does not read from either side, so the sender's data waits where it is and no answer comes back.
    client.pipe(upstream);
    upstream.pipe(client);
    if (this.#frozen) this.#hold(pair);
  }

  #hold({ client, upstream }) {
    client.unpipe(upstream);
    upstream.unpipe(client);
    client.pause();
    upstream.pause();
  }

  /** Stops passing anything; the connections stay open. */
  freeze() {
    this.#frozen = true;
    for (const pair of this.#pairs) this.#hold(pair);
  }

  /** Passes everything again, including what waited. */
  thaw() {
    this.#frozen = false;
    for (const { client, upstream } of this.#pairs) {
      client.pipe(upstream);
      upstream.pipe(client);
      client.resume();
      upstream.resume();
    }
  }

  async close() {
    for (const { client, upstream } of this.#pairs) {
      client.destroy();
      upstream.destroy();
    }
    await new Promise((resolve) => this.#server.close(resolve));
  }
}
