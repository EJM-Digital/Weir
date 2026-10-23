import { screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
  stubActivityStream,
  type ActivityStreamHandle,
} from "../../../../test/fake-activity-stream";
import {
  elapse,
  useFakeClock,
  useRealClock,
} from "../../../../test/fake-clock";
import { systemReplies } from "../../../../test/recorded-system";
import type { RecordedWeir } from "../../../../test/recorded-weir";
import { renderLogList, serveLog } from "../../../../test/log-wire-support";

const LOG_PATH = "/api/v1/system/log";
/** The server log refreshes at most this often however many lines arrive (use-log-entries.ts). */
const THROTTLE_MS = 3_000;

let weir: RecordedWeir;
let stream: ActivityStreamHandle;

const readsOfTheLog = () => weir.sent("GET", LOG_PATH).length;
const shownLines = () => screen.queryAllByTestId("log-row").length;
const lineWritten = () =>
  stream.logLineWritten(systemReplies.systemLogFrame.body);

beforeEach(async () => {
  useFakeClock({ now: new Date("2026-10-15T19:02:00Z") });
  weir = serveLog({
    [`GET ${LOG_PATH}`]: [
      systemReplies.serverLogBefore,
      systemReplies.serverLogAfter,
    ],
  });
  stream = stubActivityStream();
  renderLogList();
  await waitFor(() => expect(shownLines()).toBe(42));
});

afterEach(() => {
  useRealClock();
  vi.unstubAllGlobals();
});

describe("the Server log while it is open", () => {
  it("reads the log again three seconds after the server writes a line, and shows the new lines", async () => {
    expect(readsOfTheLog()).toBe(1);

    lineWritten();
    await elapse(THROTTLE_MS - 1);
    expect(readsOfTheLog()).toBe(1);
    await elapse(1);

    expect(readsOfTheLog()).toBe(2);
    await waitFor(() => expect(shownLines()).toBe(45));
  });

  it("reads once for a burst of lines, not once for each", async () => {
    for (let line = 0; line < 3; line++) {
      lineWritten();
      await elapse(1_000);
    }

    expect(readsOfTheLog()).toBe(2);
    await elapse(THROTTLE_MS);
    expect(readsOfTheLog()).toBe(2);
  });

  it("reads again for a line that comes after that read", async () => {
    lineWritten();
    await elapse(THROTTLE_MS);
    expect(readsOfTheLog()).toBe(2);

    lineWritten();
    await elapse(THROTTLE_MS);

    expect(readsOfTheLog()).toBe(3);
  });

  it("does not read the log for a frame it cannot understand", async () => {
    stream.logLineWritten("not a frame");
    await elapse(THROTTLE_MS * 2);

    expect(readsOfTheLog()).toBe(1);
  });
});
