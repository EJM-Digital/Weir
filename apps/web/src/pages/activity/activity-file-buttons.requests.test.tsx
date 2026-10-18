import { fireEvent, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { processingFile } from "../../test/dashboard-fixtures";
import { recordedBody, recordedResponse } from "../../test/recorded";
import {
  jsonAnswer,
  savedFields,
  type StubbedApi,
} from "../../test/stub-weir-api";
import { renderActivityPage, stubActivityServer } from "./activity-test-server";

const MOVE_TO_TOP = "POST /api/v1/processing/files/1/move-to-top";
const WHY_HELD = "GET /api/v1/processing/files/1/why-held";
const NO_SUCH_FILE = "GET /api/v1/processing/files/9999/why-held";
const REQUEUE = "POST /api/v1/processing/files/1/requeue";
const SCAN_DISPATCH =
  "POST /api/v1/processing/jobs/watched-folder-remux-scan-dispatch/enqueue";

const WAITING = processingFile({
  id: 1,
  relative_path: "Recorded Film (2020).mkv",
  status: "unprocessed",
  status_reason: "Waiting for its turn.",
});
const HELD = { ...WAITING, status: "on_hold" as const };
const FAILED = { ...WAITING, status: "processing_failed" as const };

let api: StubbedApi;

/** An answer that waits until the test lets it go, so the page can be seen while the server is busy. */
function gate(answer: () => Response) {
  let open: () => void = () => undefined;
  const opened = new Promise<void>((resolve) => (open = resolve));
  return {
    open,
    reply: async () => {
      await opened;
      return answer();
    },
  };
}

/** What the actions say about the last thing they did. */
const notice = async () =>
  within(await screen.findByTestId("activity-file-actions")).findByRole(
    "status",
  );

const button = (name: string) => screen.getByRole("button", { name });
const everyActionDisabled = () =>
  screen
    .getAllByRole("button")
    .filter((b) => b.closest("[data-testid=activity-file-actions]"))
    .every((b) => (b as HTMLButtonElement).disabled);

beforeEach(() => {
  localStorage.clear();
  vi.stubGlobal("matchMedia", () => ({
    matches: false,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  }));
});

afterEach(() => {
  expect(api.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

describe("Move to top", () => {
  it("asks the server to put the file first and shows what the server said", async () => {
    api = stubActivityServer({
      files: [WAITING],
      overrides: { [MOVE_TO_TOP]: recordedResponse(MOVE_TO_TOP) },
    });
    renderActivityPage("/activity?file=1");

    fireEvent.click(await screen.findByRole("button", { name: "Move to top" }));

    expect(await notice()).toHaveTextContent(
      recordedBody<{ detail: string }>(MOVE_TO_TOP).detail,
    );
    expect(api.to(MOVE_TO_TOP)).toHaveLength(1);
  });

  it("says the file could not be moved when the server fails", async () => {
    api = stubActivityServer({
      files: [WAITING],
      overrides: {
        [MOVE_TO_TOP]: jsonAnswer({ detail: "Weir is busy." }, 503),
      },
    });
    renderActivityPage("/activity?file=1");

    fireEvent.click(await screen.findByRole("button", { name: "Move to top" }));

    expect(await notice()).toHaveTextContent(
      "Could not move that file to the front of the queue.",
    );
  });

  it("is offered only for a file that is waiting, not for a failed or held one", async () => {
    api = stubActivityServer({ files: [FAILED] });
    renderActivityPage("/activity?file=1");

    await screen.findByRole("button", { name: "Try again" });
    expect(screen.queryByRole("button", { name: "Move to top" })).toBeNull();
  });
});

describe("Check again", () => {
  it("queues a scan of the file's workflow as a movie workflow and says so", async () => {
    api = stubActivityServer({
      files: [HELD],
      overrides: { [SCAN_DISPATCH]: recordedResponse(SCAN_DISPATCH) },
    });
    renderActivityPage("/activity?file=1");

    fireEvent.click(await screen.findByRole("button", { name: "Check again" }));

    expect(await notice()).toHaveTextContent(
      "Weir is checking this workflow again and will queue the file when it is ready.",
    );
    expect(savedFields(api.to(SCAN_DISPATCH)[0])).toEqual({
      enqueue_remux_jobs: true,
      media_scope: "movie",
      library_id: 1,
    });
  });

  it("scans a TV workflow as TV", async () => {
    api = stubActivityServer({
      files: [{ ...HELD, library_id: 2, library_name: "TV" }],
      overrides: { [SCAN_DISPATCH]: recordedResponse(SCAN_DISPATCH) },
    });
    renderActivityPage("/activity?file=1");

    fireEvent.click(await screen.findByRole("button", { name: "Check again" }));

    await notice();
    expect(savedFields(api.to(SCAN_DISPATCH)[0])).toMatchObject({
      media_scope: "tv",
      library_id: 2,
    });
  });

  it("says the workflow could not be checked, and where to look, when the server fails", async () => {
    api = stubActivityServer({
      files: [HELD],
      overrides: {
        [SCAN_DISPATCH]: jsonAnswer({ detail: "Weir is busy." }, 503),
      },
    });
    renderActivityPage("/activity?file=1");

    fireEvent.click(await screen.findByRole("button", { name: "Check again" }));

    expect(await notice()).toHaveTextContent(
      "Could not queue watched-folder scan.",
    );
  });
});

describe("Why is this held?", () => {
  it("shows the reasons the media managers gave, as the server worded them", async () => {
    api = stubActivityServer({
      files: [HELD],
      overrides: { [WHY_HELD]: recordedResponse(WHY_HELD) },
    });
    renderActivityPage("/activity?file=1");

    fireEvent.click(
      await screen.findByRole("button", { name: "Why is this held?" }),
    );

    expect(await notice()).toHaveTextContent(
      recordedBody<{ reasons: string[] }>(WHY_HELD).reasons[0],
    );
  });

  it("says the managers had nothing to say when they gave no reason", async () => {
    api = stubActivityServer({
      files: [HELD],
      overrides: {
        [WHY_HELD]: {
          ...recordedBody<Record<string, unknown>>(WHY_HELD),
          reasons: [],
        },
      },
    });
    renderActivityPage("/activity?file=1");

    fireEvent.click(
      await screen.findByRole("button", { name: "Why is this held?" }),
    );

    expect(await notice()).toHaveTextContent(
      "The media managers had nothing to say about this file.",
    );
  });

  it("says what the server said when it has no record of the file", async () => {
    api = stubActivityServer({
      files: [HELD],
      overrides: { [WHY_HELD]: recordedResponse(NO_SUCH_FILE) },
    });
    renderActivityPage("/activity?file=1");

    fireEvent.click(
      await screen.findByRole("button", { name: "Why is this held?" }),
    );

    expect(await notice()).toHaveTextContent(
      recordedBody<{ detail: string }>(NO_SUCH_FILE).detail,
    );
  });
});

describe("while one action runs", () => {
  it.each([
    [
      "Check again",
      "Checking…",
      HELD,
      SCAN_DISPATCH,
      recordedResponse(SCAN_DISPATCH),
    ],
    [
      "Why is this held?",
      "Asking…",
      HELD,
      WHY_HELD,
      recordedResponse(WHY_HELD),
    ],
    [
      "Move to top",
      "Moving…",
      WAITING,
      MOVE_TO_TOP,
      recordedResponse(MOVE_TO_TOP),
    ],
    ["Try again", "Queueing…", FAILED, REQUEUE, recordedResponse(REQUEUE)],
  ])(
    "%s says %s, holds every other action, and lets them go when the server has answered",
    async (label, doing, file, route, reply) => {
      const answer = gate(() => reply);
      api = stubActivityServer({
        files: [file],
        overrides: { [route]: answer.reply },
      });
      renderActivityPage("/activity?file=1");

      fireEvent.click(await screen.findByRole("button", { name: label }));

      expect(await screen.findByRole("button", { name: doing })).toBeDisabled();
      expect(everyActionDisabled()).toBe(true);
      answer.open();
      await notice();
      expect(button(label)).toBeEnabled();
      expect(everyActionDisabled()).toBe(false);
    },
  );
});
