import type { RecordedReply } from "./recorded-weir";

/**
 * A route that keeps its answer until the test lets it go, so what the page shows while it waits can be read. Give
 * `route` to `serveRecordedReplies` and call `release` when the server should answer.
 */
export function heldReply(reply: RecordedReply): {
  route: () => Promise<RecordedReply>;
  release: () => void;
} {
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  return {
    release,
    route: async () => {
      await gate;
      return reply;
    },
  };
}
