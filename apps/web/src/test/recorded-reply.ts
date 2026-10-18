import { recordedBody, recordedStatus } from "./recorded";
import type { RecordedReply } from "./recorded-weir";

/** What the server answered `METHOD /path?query` with when it was recorded, as a reply `serveRecordedReplies` can give. */
export function recordedReply(route: string): RecordedReply {
  const body = recordedBody<unknown>(route);
  return {
    status: recordedStatus(route),
    body: body === null ? "" : JSON.stringify(body),
  };
}
