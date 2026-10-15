import type {
  ProcessingFile,
  ProcessingFilesPage,
} from "../../lib/processing/files-api";
import activity from "./activity.json?raw";
import connections from "./connections.json?raw";
import processing from "./processing.json?raw";
import session from "./session.json?raw";

/**
 * What a running Weir server answered to the requests the web app makes, kept as it sent them: the status, and the
 * body parsed (null for an answer with none). They were taken from a server built from this repository, run on
 * localhost with two-track test files made by FFmpeg, by `apps/web/scripts/record-replies.mjs`. Nothing in them is
 * written by hand; run the script again to refresh them.
 */
type RecordedAnswer = { status: number; body: unknown };

const recordings: Record<string, RecordedAnswer> = Object.assign(
  {},
  ...[activity, connections, processing, session].map(
    (text) => JSON.parse(text) as Record<string, RecordedAnswer>,
  ),
);

function answerTo(route: string): RecordedAnswer {
  const answer = recordings[route];
  if (!answer) {
    throw new Error(`No recording answers ${route}.`);
  }
  return answer;
}

/** The body the server sent to `METHOD /path?query`, as the web app reads it. */
export function recordedBody<Body>(route: string): Body {
  return answerTo(route).body as Body;
}

/** The whole answer to `METHOD /path?query`, for a stub that answers a request with it. */
export function recordedResponse(route: string): Response {
  const { status, body } = answerTo(route);
  return new Response(body === null ? null : JSON.stringify(body), {
    status,
    headers: body === null ? {} : { "Content-Type": "application/json" },
  });
}

/**
 * A file as the server listed it once it was processed, whole: every field it sends, with the values it sent. A test says
 * only what it needs different from it.
 */
export function recordedProcessingFile(): ProcessingFile {
  return structuredClone(
    recordedBody<ProcessingFilesPage>("GET /api/v1/processing/files").files[0]!,
  );
}

/** The status the server answered `METHOD /path?query` with. */
export function recordedStatus(route: string): number {
  return answerTo(route).status;
}

/** Every route the server was recorded answering. */
export function recordedRoutes(): string[] {
  return Object.keys(recordings);
}
