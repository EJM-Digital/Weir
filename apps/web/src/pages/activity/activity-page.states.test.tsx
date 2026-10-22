import {
  cleanup,
  fireEvent,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { KeptFile } from "../../lib/processing/kept-files-api";
import { filesPage, processingFile } from "../../test/dashboard-fixtures";
import { recordedResponse } from "../../test/recorded";
import type { StubbedApi, StubbedRequest } from "../../test/stub-weir-api";
import {
  FILES,
  KEPT,
  never,
  renderActivityPage,
  stubActivityServer,
} from "./activity-test-server";

const REMOVE_OPTIONS = "GET /api/v1/processing/files/1/remove-options";
const FORGET = "DELETE /api/v1/processing/files/1";
const REMOVED = "Removed from the list. The file on disk is untouched.";

const ALPHA = processingFile({
  id: 1,
  relative_path: "Alpha Station/alpha.mkv",
  status: "processed",
  status_reason: "Finished processing this file.",
  updated_at: "2026-10-03T10:00:00Z",
});
const ZED = processingFile({
  id: 2,
  relative_path: "Zed Coast/zed.mkv",
  status: "processing_failed",
  updated_at: "2026-10-01T10:00:00Z",
});

const OTHER = processingFile({
  id: 3,
  relative_path: "Other Bay/other.mkv",
  status: "processed",
  updated_at: "2026-09-30T10:00:00Z",
});

let api: StubbedApi;

const notice = () => screen.queryByText(REMOVED);
const rowOf = (path: string) =>
  screen.getByTitle(path).closest("tr") as HTMLElement;

beforeEach(() => {
  localStorage.clear();
  vi.stubGlobal("matchMedia", () => ({
    matches: false,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  }));
});

afterEach(() => {
  // The page comes down while the stubs are still in place: a page left up would ask the real fetch and matchMedia
  // for what a late answer brings, and fail the test that happens to be running then.
  cleanup();
  try {
    expect(api.unmatched).toEqual([]);
  } finally {
    vi.unstubAllGlobals();
  }
});

describe("while Activity is reading", () => {
  it("says it is reading activity until the files arrive", async () => {
    api = stubActivityServer({ files: [ALPHA], overrides: { [FILES]: never } });

    renderActivityPage();

    expect(await screen.findByText("Reading activity…")).toBeVisible();
    expect(screen.queryByRole("region", { name: "Files" })).toBeNull();
  });

  it("says it is reading kept files until the kept files arrive", async () => {
    api = stubActivityServer({ files: [ALPHA], overrides: { [KEPT]: never } });

    renderActivityPage("/activity?show=kept");

    expect(await screen.findByText("Reading kept files…")).toBeVisible();
  });
});

describe("when the server sends as many downloads as Activity asks for", () => {
  const full = (request: StubbedRequest) => ({
    ...filesPage(request.query.has("file_status") ? [] : [ALPHA]),
    returned: request.query.has("file_status") ? 0 : 1000,
  });

  it("says the list is the newest 1,000 downloads, and how to see more", async () => {
    api = stubActivityServer({
      files: [ALPHA],
      overrides: { [FILES]: full },
    });

    renderActivityPage();

    expect(
      await screen.findByText(
        "Showing the newest 1,000 downloads. Narrow the search or the period to see more.",
      ),
    ).toBeVisible();
  });

  it("says the first 1,000 in this order once the list is sorted another way", async () => {
    api = stubActivityServer({
      files: [ALPHA],
      overrides: { [FILES]: full },
    });
    renderActivityPage();
    await screen.findByText(/^Showing the newest/);

    fireEvent.click(
      within(screen.getByRole("columnheader", { name: /^File/ })).getByRole(
        "button",
      ),
    );

    expect(
      await screen.findByText(
        "Showing the first 1,000 downloads in this order. Narrow the search or the period to see more.",
      ),
    ).toBeVisible();
  });

  it("says nothing about a cap when fewer come back", async () => {
    api = stubActivityServer({ files: [ALPHA] });

    renderActivityPage();

    await screen.findByRole("region", { name: "Files" });
    expect(screen.queryByText(/Showing the/)).toBeNull();
  });
});

describe("the list of files by keyboard", () => {
  it("makes each file a real button that Tab reaches and that opens the file when activated", async () => {
    api = stubActivityServer({ files: [ALPHA, ZED] });
    renderActivityPage("/activity?file=1");
    await screen.findByTestId("activity-detail");

    const open = within(rowOf("Zed Coast/zed.mkv")).getByRole("button");
    expect(open.tagName).toBe("BUTTON");
    expect(open).toHaveAttribute("type", "button");
    expect(open.tabIndex).toBe(0);
    open.focus();
    expect(open).toHaveFocus();
    open.click();

    expect(
      await screen.findByRole("region", { name: "zed.mkv" }),
    ).toBeVisible();
    expect(rowOf("Zed Coast/zed.mkv")).toHaveAttribute("aria-current", "true");
    expect(rowOf("Alpha Station/alpha.mkv")).not.toHaveAttribute(
      "aria-current",
    );
  });
});

describe("after a file is removed from the list", () => {
  /** A server on which removing the first file takes it out of the list. */
  function serverRemovingAlpha(remaining = [ZED]) {
    let removed = false;
    api = stubActivityServer({
      files: [ALPHA, ...remaining],
      overrides: {
        [FILES]: (request: StubbedRequest) =>
          filesPage(
            request.query.has("file_status")
              ? []
              : removed
                ? remaining
                : [ALPHA, ...remaining],
          ),
        [REMOVE_OPTIONS]: recordedResponse(REMOVE_OPTIONS),
        [FORGET]: () => {
          removed = true;
          return new Response(null, { status: 204 });
        },
      },
    });
  }

  async function removeAlpha() {
    renderActivityPage("/activity?file=1");
    fireEvent.click(
      await screen.findByRole("button", { name: "Remove from list" }),
    );
    await screen.findAllByText(REMOVED);
  }

  it("says so on the page and opens the next file in the list", async () => {
    serverRemovingAlpha();

    await removeAlpha();

    expect(
      await screen.findByRole("region", { name: "zed.mkv" }),
    ).toBeVisible();
    expect(screen.getAllByText(REMOVED)).toHaveLength(1);
    expect(rowOf("Zed Coast/zed.mkv")).toHaveAttribute("aria-current", "true");
    expect(screen.queryByTitle("Alpha Station/alpha.mkv")).toBeNull();
  });

  it("clears the notice when another file is picked", async () => {
    serverRemovingAlpha([ZED, OTHER]);
    await removeAlpha();

    fireEvent.click(within(rowOf("Other Bay/other.mkv")).getByRole("button"));

    await waitFor(() => expect(notice()).toBeNull());
    expect(
      await screen.findByRole("region", { name: "other.mkv" }),
    ).toBeVisible();
  });

  it("clears the notice when a filter changes", async () => {
    serverRemovingAlpha();
    await removeAlpha();

    fireEvent.click(screen.getByRole("button", { name: /^Failed/ }));

    await waitFor(() => expect(notice()).toBeNull());
  });
});

describe("the kept files", () => {
  const kept = (
    id: number,
    path: string,
    workflow: string,
    size: number,
  ): KeptFile => ({
    id,
    library_id: 1,
    library_name: workflow,
    relative_path: path,
    size_bytes: size,
    kept_at: "2026-09-26T00:00:00",
  });
  const KEPT_FILES = [
    kept(10, "Mid/mid.mkv", "Movies", 2_000_000),
    kept(11, "Low/low.mkv", "TV", 3_000_000),
    kept(12, "High/high.mkv", "Anime", 1_000_000),
  ];
  const keptPaths = () =>
    screen
      .getAllByTestId(/^kept-file-row-/)
      .map((row) => row.getAttribute("data-testid"));
  const sortBy = (heading: string) =>
    fireEvent.click(
      within(
        screen.getByRole("columnheader", { name: new RegExp(`^${heading}`) }),
      ).getByRole("button"),
    );

  beforeEach(() => {
    api = stubActivityServer({
      files: [ALPHA],
      overrides: { [KEPT]: { files: KEPT_FILES } },
    });
  });

  it("lists them in the order the server gave until a heading is chosen", async () => {
    renderActivityPage("/activity?show=kept");

    await screen.findByTestId("kept-files-table");

    expect(keptPaths()).toEqual([
      "kept-file-row-10",
      "kept-file-row-11",
      "kept-file-row-12",
    ]);
  });

  it("says what the server answered to Process again, until another kind of file is shown", async () => {
    const answer =
      "Weir is checking this file's workflow now and will queue it once it is ready.";
    api = stubActivityServer({
      files: [ALPHA],
      overrides: {
        [KEPT]: { files: KEPT_FILES },
        "POST /api/v1/processing/kept-files/10/process-again": {
          detail: answer,
        },
      },
    });
    renderActivityPage("/activity?show=kept");
    const row = await screen.findByTestId("kept-file-row-10");

    fireEvent.click(within(row).getByRole("button", { name: "Process again" }));

    expect(await screen.findByText(answer)).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: /^Failed/ }));
    await waitFor(() => expect(screen.queryByText(answer)).toBeNull());
  });

  it("sorts by File, and reverses on the second click", async () => {
    renderActivityPage("/activity?show=kept");
    await screen.findByTestId("kept-files-table");

    sortBy("File");
    expect(keptPaths()).toEqual([
      "kept-file-row-12",
      "kept-file-row-11",
      "kept-file-row-10",
    ]);
    sortBy("File");
    expect(keptPaths()).toEqual([
      "kept-file-row-10",
      "kept-file-row-11",
      "kept-file-row-12",
    ]);
  });

  it("sorts by Workflow by name", async () => {
    renderActivityPage("/activity?show=kept");
    await screen.findByTestId("kept-files-table");

    sortBy("Workflow");

    expect(keptPaths()).toEqual([
      "kept-file-row-12",
      "kept-file-row-10",
      "kept-file-row-11",
    ]);
  });

  it("sorts by Size, largest first on the first click", async () => {
    renderActivityPage("/activity?show=kept");
    await screen.findByTestId("kept-files-table");

    sortBy("Size");
    expect(keptPaths()).toEqual([
      "kept-file-row-11",
      "kept-file-row-10",
      "kept-file-row-12",
    ]);
    sortBy("Size");
    expect(keptPaths()).toEqual([
      "kept-file-row-12",
      "kept-file-row-10",
      "kept-file-row-11",
    ]);
  });
});
