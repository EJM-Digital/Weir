// What the recording proxy keeps of one request and its reply: the headers a replay needs with every secret scrubbed, and the body as the
// application sent it. The recordings are written in the format of the other recordings in apps/server/tests/fixtures/recorded.

/** The request headers a recording keeps, lower case. Anything else (host, length, the client's own bookkeeping) is dropped. */
const REQUEST_HEADERS = ["content-type", "idempotency-key", "x-webhook-secret", "x-api-key", "x-requested-with", "if-match"];
/** The reply headers a recording keeps. */
const REPLY_HEADERS = ["content-type", "retry-after", "location"];
const SECRET_HEADERS = new Set(["x-webhook-secret", "x-api-key", "authorization", "cookie", "set-cookie"]);
export const SCRUBBED = "<scrubbed>";

/** The headers a recording keeps, in the order they were sent, a secret replaced. */
export function keptHeaders(headers, allowed) {
  const kept = {};
  for (const [name, value] of Object.entries(headers)) {
    const lower = name.toLowerCase();
    if (!allowed.includes(lower)) continue;
    kept[lower] = SECRET_HEADERS.has(lower) ? SCRUBBED : Array.isArray(value) ? value.join(", ") : String(value);
  }
  return kept;
}

/** Members of a body that name the machine an application runs on, which a recording does not keep. */
const MACHINE_MEMBERS = new Set(["machine_name", "machineName", "instanceName", "hostName"]);

function scrubbedMachine(value) {
  if (Array.isArray(value)) return value.map(scrubbedMachine);
  if (value !== null && typeof value === "object") return Object.fromEntries(Object.entries(value).map(([key, inner]) => [key, MACHINE_MEMBERS.has(key) ? SCRUBBED : scrubbedMachine(inner)]));
  return value;
}

/** `{body}` for JSON, `{bodyText}` for any other text, nothing for no body. */
export function bodyOf(buffer, contentType) {
  if (!buffer || buffer.length === 0) return {};
  const text = buffer.toString("utf8");
  if (/json/i.test(contentType ?? "")) {
    try {
      return { body: scrubbedMachine(JSON.parse(text)) };
    } catch {
      return { bodyText: text };
    }
  }
  return { bodyText: text };
}

/**
 * @param {{seq: number, route: string, at: string, method: string, path: string, request: object, requestBody: Buffer, status: number, reply: object, replyBody: Buffer}} seen
 * @returns the exchange as the proxy lists it: `route` says which way it went ("to-deluno" is Weir calling Deluno, "to-weir" Deluno calling Weir).
 */
export function exchangeOf(seen) {
  const requestType = seen.request["content-type"];
  const replyType = seen.reply["content-type"];
  return {
    seq: seen.seq,
    route: seen.route,
    at: seen.at,
    request: { method: seen.method, path: seen.path, headers: keptHeaders(seen.request, REQUEST_HEADERS), ...bodyOf(seen.requestBody, requestType) },
    reply: { status: seen.status, headers: keptHeaders(seen.reply, REPLY_HEADERS), ...bodyOf(seen.replyBody, replyType) },
  };
}
