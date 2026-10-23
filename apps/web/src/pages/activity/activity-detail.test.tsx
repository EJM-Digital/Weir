import { screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ProcessingDirectPlay } from "../../lib/processing/files-api";
import type { LibraryClean } from "../../lib/processing/library-cleans-api";
import { processingFile } from "../../test/dashboard-fixtures";
import { unexpectedProblem, type StubbedApi } from "../../test/stub-weir-api";
import {
  emptyLog,
  logRoute,
  never,
  renderActivityPage,
  stubActivityServer,
} from "../../test/activity-test-server";

const FINISHED = processingFile({
  id: 1,
  relative_path: "Alpha Station/alpha.mkv",
  status: "processed",
  status_reason: "Finished processing this file.",
  updated_at: "2026-10-01T10:00:00Z",
});
const RUNNING = processingFile({
  id: 2,
  relative_path: "Beta Coast/beta.mkv",
  status: "processing",
  status_reason: "Weir is working on this file.",
  progress_percent: 42.4,
  progress_speed: "148x",
  progress_elapsed_seconds: 125,
  progress_eta_seconds: 300,
  updated_at: "2026-10-02T10:00:00Z",
});
const CLEAN: LibraryClean = {
  kind: "library_clean",
  id: 5,
  library_id: 1,
  library_name: "Movies",
  relative_path: "Heat (1995)/Heat.mkv",
  outcome: "cleaned",
  detail: "Cleaned Heat.mkv: removed 1 audio track.",
  trigger: null,
  recorded_at: "2026-10-02T10:00:00Z",
};

let api: StubbedApi;
const detail = () => screen.findByTestId("activity-detail");

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

describe("the open file's detail", () => {
  it("is a region labelled by the file's name, whose title takes focus by script but not by Tab", async () => {
    api = stubActivityServer({ files: [FINISHED] });
    renderActivityPage("/activity?file=1");

    const region = await screen.findByRole("region", { name: "alpha.mkv" });
    const title = within(region).getByRole("heading", { name: "alpha.mkv" });
    expect(region).toBe(await detail());
    expect(title).toHaveAttribute("tabindex", "-1");
  });

  it("names the Direct Play verdict of each of the person's devices", async () => {
    const directPlay: ProcessingDirectPlay[] = [
      {
        device_id: "apple_tv_4k",
        device_name: "Apple TV 4K",
        verdict: "yes",
        reasons: [],
      },
      {
        device_id: "lg_webos",
        device_name: "LG TV (webOS 25)",
        verdict: "no",
        reasons: ["cannot play DTS audio"],
      },
    ];
    api = stubActivityServer({
      files: [{ ...FINISHED, direct_play: directPlay }],
    });
    renderActivityPage("/activity?file=1");

    const line = await screen.findByTestId("activity-direct-play");
    expect(line).toHaveTextContent("Direct Play:");
    expect(line).toHaveTextContent("Apple TV 4K");
    expect(line).toHaveTextContent("LG TV (webOS 25)");
    expect(line).toHaveTextContent("DTS audio");
  });

  it("shows no Direct Play line for a file measured against no device", async () => {
    api = stubActivityServer({ files: [FINISHED] });
    renderActivityPage("/activity?file=1");

    await detail();
    expect(screen.queryByTestId("activity-direct-play")).toBeNull();
  });
});

describe("a file Weir is working on", () => {
  it("shows how far through the file it is, how fast, for how long and how long is left", async () => {
    api = stubActivityServer({ files: [RUNNING] });
    renderActivityPage("/activity?file=2");

    const bar = await screen.findByRole("progressbar", {
      name: "Through the file",
    });
    expect(bar).toHaveAttribute("aria-valuenow", "42");
    const figures = bar.closest(".mm-history-progress") as HTMLElement;
    const figure = (term: string) =>
      within(figures).getByText(term).nextElementSibling;
    expect(figure("Through the file")).toHaveTextContent("42%");
    expect(figure("Speed")).toHaveTextContent("148x");
    expect(figure("Running for")).toHaveTextContent("2 min 5 s");
    expect(figure("About")).toHaveTextContent("5 min 0 s left");
  });

  it("leaves out the speed, the time running and the time left that the server has not measured", async () => {
    api = stubActivityServer({
      files: [
        {
          ...RUNNING,
          progress_speed: null,
          progress_elapsed_seconds: null,
          progress_eta_seconds: null,
        },
      ],
    });
    renderActivityPage("/activity?file=2");

    const bar = await screen.findByRole("progressbar");
    const figures = bar.closest(".mm-history-progress") as HTMLElement;
    expect(within(figures).queryByText("Speed")).toBeNull();
    expect(within(figures).queryByText("Running for")).toBeNull();
    expect(within(figures).queryByText("About")).toBeNull();
  });

  it("offers the Dashboard, where the running file can be watched, and no size figures yet", async () => {
    api = stubActivityServer({ files: [RUNNING] });
    renderActivityPage("/activity?file=2");

    const link = await screen.findByRole("link", {
      name: "See it on the Dashboard →",
    });
    expect(link).toHaveAttribute("href", "/");
    expect(screen.queryByTestId("activity-sizes")).toBeNull();
  });

  it("offers no Dashboard link for a file that is not running, which shows its sizes instead", async () => {
    api = stubActivityServer({ files: [FINISHED] });
    renderActivityPage("/activity?file=1");

    await detail();
    expect(
      screen.queryByRole("link", { name: /See it on the Dashboard/ }),
    ).toBeNull();
    expect(screen.getByTestId("activity-sizes")).toBeInTheDocument();
  });
});

describe("the record of what Weir did to the open file", () => {
  describe("how long the last pass took", () => {
    const passLog = (file: typeof FINISHED) => ({
      ...emptyLog(file),
      entries: [
        {
          id: 1,
          recorded_at: "2026-10-01T10:00:00Z",
          outcome: "live_output_written",
          title: "Processed",
          library_name: "Movies",
          detail: { outcome: "live_output_written", elapsed_seconds: 125 },
          story: [],
        },
      ],
    });

    it("is added to the lead of a finished file", async () => {
      api = stubActivityServer({
        files: [FINISHED],
        overrides: { [logRoute(1)]: passLog(FINISHED) },
      });
      renderActivityPage("/activity?file=1");

      await waitFor(async () =>
        expect(await detail()).toHaveTextContent("Took 2 min 5 s."),
      );
    });

    it("is left out while the file is being worked on again", async () => {
      api = stubActivityServer({
        files: [RUNNING],
        overrides: { [logRoute(2)]: passLog(RUNNING) },
      });
      renderActivityPage("/activity?file=2");

      const region = await detail();
      await waitFor(() =>
        expect(screen.queryByText("Reading this file's record…")).toBeNull(),
      );
      expect(region).not.toHaveTextContent("Took");
    });
  });

  it("says it is reading the record while the server has not answered", async () => {
    api = stubActivityServer({
      files: [FINISHED],
      overrides: { [logRoute(1)]: never },
    });
    renderActivityPage("/activity?file=1");

    expect(
      await screen.findByText("Reading this file's record…"),
    ).toBeVisible();
  });

  it("says Weir has no record of a finished file whose record is gone, and names the setting that removes records", async () => {
    api = stubActivityServer({ files: [FINISHED] });
    renderActivityPage("/activity?file=1");

    expect(
      await screen.findByText(
        "Weir has no record of what it did to this file. Records older than the File activity setting are removed.",
      ),
    ).toBeVisible();
  });

  it("says a running file has no record yet because Weir has not finished with it", async () => {
    api = stubActivityServer({ files: [RUNNING] });
    renderActivityPage("/activity?file=2");

    expect(
      await screen.findByText(
        "Weir has not finished processing this file yet. What it kept and removed shows here once it has.",
      ),
    ).toBeVisible();
  });

  it("says the record could not be read when the server fails, and never that there is none", async () => {
    api = stubActivityServer({ files: [FINISHED] });
    api.set(logRoute(1), unexpectedProblem());
    renderActivityPage("/activity?file=1");

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Weir couldn't load this file's record.",
    );
    expect(screen.queryByText(/Weir has no record of what it did/)).toBeNull();
  });

  it("links to the download of the file's whole record", async () => {
    api = stubActivityServer({ files: [FINISHED] });
    renderActivityPage("/activity?file=1");

    const link = await screen.findByRole("link", {
      name: "Download its record →",
    });
    expect(link).toHaveAttribute(
      "href",
      "/api/v1/processing/files/1/log/download",
    );
  });

  it("says how long Weir keeps records, from the record the server sent", async () => {
    api = stubActivityServer({
      files: [FINISHED],
      overrides: {
        [logRoute(1)]: { ...emptyLog(FINISHED), retention_days: 30 },
      },
    });
    renderActivityPage("/activity?file=1");

    expect(
      await screen.findByTestId("activity-retention-note"),
    ).toHaveTextContent("30");
  });
});

describe("a library clean's detail", () => {
  it("is headed by the file, leads with the outcome and the day it was recorded, and gives the clean's own words", async () => {
    api = stubActivityServer({ files: [], cleans: [CLEAN] });
    renderActivityPage("/activity?clean=5");

    const region = await screen.findByRole("region", { name: "Heat.mkv" });
    expect(within(region).getByText("Movies")).toBeVisible();
    expect(region).toHaveTextContent("Cleaned in place.");
    expect(region).toHaveTextContent("Oct");
    expect(region).toHaveTextContent(
      "Cleaned Heat.mkv: removed 1 audio track.",
    );
  });

  it.each([
    ["skipped", "Already matched the workflow's rules."],
    ["failed", "The clean failed."],
  ] as const)("leads with what a %s clean means", async (outcome, lead) => {
    api = stubActivityServer({ files: [], cleans: [{ ...CLEAN, outcome }] });
    renderActivityPage("/activity?clean=5");

    expect(await detail()).toHaveTextContent(lead);
  });

  it("offers no action on a clean, which is run again from its library", async () => {
    api = stubActivityServer({ files: [], cleans: [CLEAN] });
    renderActivityPage("/activity?clean=5");

    await detail();
    expect(screen.queryByTestId("activity-file-actions")).toBeNull();
  });
});
