// What the recording proxy saw, made into recordings in the format of apps/server/tests/fixtures/recorded/deluno-side:
// `{app, version, state, recordedAt, note, exchanges[{request{method, path, headers, body}, reply{status, headers, body}}]}`, one file per state
// under a folder named for the version Deluno reported. A recording is the conversation of one hand-off, or the exchanges of connecting the two
// applications; nothing in it is written by a person.
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";

const MANIFEST_PATH = "/api/integrations/external/manifest";
/** Routes that are only a heartbeat or a readiness probe: they say the same thing every time and belong to no conversation. */
const NOISE = [/^\/ready(\?|$)/, /^\/health(\?|$)/, /^\/api\/integrations\/external\/health(\?|$)/];

/** Routes that belong to a hand-off, whichever one: Deluno handing one over, asking about it, cancelling it or telling Weir its outcome, and Weir's reports. */
const HANDOFF_ROUTES = [/^\/api\/v1\/intake\/(webhook|handoffs)\//, /^\/api\/integrations\/processors\/events(\?|$)/];

const pathOf = (exchange) => exchange.request.path.split("?")[0];
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);

/** The version Deluno called itself in its manifest, or undefined when Weir never read the manifest. */
export function delunoVersionOf(exchanges) {
  const manifest = exchanges.find((exchange) => exchange.route === "to-deluno" && pathOf(exchange) === MANIFEST_PATH && exchange.reply.status === 200);
  return typeof manifest?.reply.body?.version === "string" ? manifest.reply.body.version : undefined;
}

/** Whether the exchange is about this hand-off: its id is in the path, the request's body or headers, or the reply's body. */
export function mentions(exchange, handoffId) {
  const { request, reply } = exchange;
  return [request.path, JSON.stringify(request.body ?? ""), JSON.stringify(request.headers ?? {}), JSON.stringify(reply.body ?? "")].some((text) => text.includes(handoffId));
}

/**
 * Exchanges that repeat the one before: the same route, request and reply (a status asked for again and answered the same). Only the first is kept,
 * so a hand-off Deluno asked about every few seconds is one question, not fifty.
 */
export function withoutRepeats(exchanges) {
  const kept = [];
  for (const exchange of exchanges) {
    const before = kept.at(-1);
    const repeat = before && before.route === exchange.route && same(before.request, exchange.request) && same(before.reply, exchange.reply);
    if (!repeat) kept.push(exchange);
  }
  return kept;
}

/** The conversation about one hand-off, in order, with repeated questions collapsed. */
export function conversationOf(exchanges, handoffId) {
  return withoutRepeats(exchanges.filter((exchange) => mentions(exchange, handoffId)));
}

/**
 * What passed between the applications that belongs to no hand-off: the first time each call was made (the manifest, what Weir can do, the
 * folders Weir watches, where downloads land), without the heartbeats and without any call about a hand-off.
 */
export function connectionExchanges(exchanges, handoffIds) {
  const seen = new Set();
  return exchanges.filter((exchange) => {
    if (handoffIds.some((id) => mentions(exchange, id)) || HANDOFF_ROUTES.some((pattern) => pattern.test(exchange.request.path))) return false;
    if (NOISE.some((pattern) => pattern.test(exchange.request.path))) return false;
    const key = `${exchange.route} ${exchange.request.method} ${pathOf(exchange)} ${exchange.reply.status}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

/** The exchanges as a recording holds them: the request and the reply, without the proxy's own numbering. */
const recorded = (exchange) => ({ request: exchange.request, reply: exchange.reply });

/** What a run asks to have kept, written when it ends from everything the proxy saw. */
export class RecordingSet {
  #wanted = [];

  /** Keeps the conversation about a hand-off under this state name. */
  keepConversation({ state, note, handoffId }) {
    this.#claim(state);
    this.#wanted.push({ state, note, handoffId });
  }

  /** Keeps the exchanges of connecting the two applications under this state name. */
  keepConnection({ state, note }) {
    this.#claim(state);
    this.#wanted.push({ state, note, connection: true });
  }

  #claim(state) {
    if (!/^[a-z0-9-]+$/.test(state)) throw new Error(`A recording's state is lower-case words joined by dashes, not ${JSON.stringify(state)}.`);
    if (this.#wanted.some((entry) => entry.state === state)) throw new Error(`A recording named ${state} is already wanted.`);
  }

  /**
   * @param {object[]} exchanges Everything the proxy saw, in order.
   * @param {string} recordedAt When this is being written (ISO).
   * @returns {{version: string, recordings: object[]}} A conversation the proxy saw nothing of throws: the scenario did not see the traffic it said it would.
   */
  build(exchanges, recordedAt) {
    if (this.#wanted.length === 0) return { version: undefined, recordings: [] };
    const version = delunoVersionOf(exchanges);
    if (!version) throw new Error("Weir never read Deluno's manifest through the recording proxy, so the version Deluno reported is not known.");
    const handoffIds = this.#wanted.filter((entry) => entry.handoffId).map((entry) => entry.handoffId);
    const recordings = this.#wanted.map((entry) => {
      const chosen = entry.connection ? connectionExchanges(exchanges, handoffIds) : conversationOf(exchanges, entry.handoffId);
      if (chosen.length === 0) throw new Error(`The recording proxy saw nothing for ${entry.state}${entry.handoffId ? ` (hand-off ${entry.handoffId})` : ""}.`);
      return { app: "deluno", version, state: entry.state, recordedAt, note: entry.note, exchanges: chosen.map(recorded) };
    });
    return { version, recordings };
  }
}

/** Writes each recording as `<folder>/<version>/<state>.json`, two-space JSON and a final newline, as the other recordings are. Returns the paths. */
export function writeRecordings(folder, recordings) {
  return recordings.map((recording) => {
    const file = join(folder, recording.version, `${recording.state}.json`);
    mkdirSync(dirname(file), { recursive: true });
    writeFileSync(file, `${JSON.stringify(recording, null, 2)}\n`);
    return file;
  });
}
