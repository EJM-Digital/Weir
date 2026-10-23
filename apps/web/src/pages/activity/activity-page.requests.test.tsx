import { fireEvent, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ProcessingFile } from "../../lib/processing/files-api";
import type { LibraryClean } from "../../lib/processing/library-cleans-api";
import { filesPage, processingFile } from "../../test/dashboard-fixtures";
import { jsonAnswer, type StubbedApi } from "../../test/stub-weir-api";
import {
  CLEANS,
  FILES,
  WORKFLOWS,
  renderActivityPage,
  stubActivityServer,
} from "../../test/activity-test-server";

const ALPHA = processingFile({
  id: 1,
  relative_path: "Alpha Station/alpha.mkv",
  status: "processed",
  status_reason: "Finished processing this file.",
  updated_at: "2026-10-01T10:00:00Z",
});
const ZED = processingFile({
  id: 2,
  relative_path: "Zed Coast/zed.mkv",
  status: "processing_failed",
  updated_at: "2026-10-03T10:00:00Z",
});
const HEAT_CLEAN: LibraryClean = {
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

function stubServer({
  files = [ALPHA, ZED],
  cleans = [HEAT_CLEAN],
  attention = [] as ProcessingFile[],
  overrides = {} as Record<string, unknown>,
} = {}) {
  api = stubActivityServer({ files, cleans, attention, overrides });
}

const renderPage = renderActivityPage;

/** The request the page made for its list of downloads (the Needs you read has its own, narrowed by status). */
const listRequests = () =>
  api.to(FILES).filter((request) => !request.query.has("file_status"));
const lastListRequest = () => listRequests().at(-1)!.query;

/** The table of files, not the timeline in the open file's detail. */
const filesTable = () =>
  within(screen.getByRole("region", { name: "Files" })).getByRole("table");

/** Waits for the table of files to be drawn from what the server answered. */
const filesLoaded = async () =>
  within(await screen.findByRole("region", { name: "Files" })).findByRole(
    "table",
  );

/** The files in the order the table lists them. */
function listedPaths(): string[] {
  const table = filesTable();
  return within(table)
    .getAllByRole("row")
    .slice(1)
    .map((row) => within(row).getAllByRole("button")[0].title);
}

const heading = (name: string) =>
  screen.getByRole("columnheader", { name: new RegExp(`^${name}`) });
const sortButton = (name: string) => within(heading(name)).getByRole("button");

beforeEach(() => {
  localStorage.clear();
  // The open file's detail asks whether the screen is narrow, to decide where focus goes.
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

describe("what Activity asks the server for, from its address", () => {
  it("asks for the last seven days of every workflow, newest first, when the address says nothing", async () => {
    stubServer();

    renderPage();

    await filesLoaded();
    const asked = lastListRequest();
    expect(Object.fromEntries(asked)).toEqual({
      within_days: "7",
      limit: "1000",
      sort: "when",
      direction: "desc",
    });
    expect(Object.fromEntries(api.to(CLEANS)[0].query)).toEqual({
      within_days: "7",
    });
  });

  it("asks for the period, the workflow and the search the address names, for the downloads and the library cleans alike", async () => {
    stubServer();

    renderPage("/activity?within=30&q=x&library=2");

    await filesLoaded();
    expect(Object.fromEntries(lastListRequest())).toEqual({
      within_days: "30",
      library_id: "2",
      path_contains: "x",
      limit: "1000",
      sort: "when",
      direction: "desc",
    });
    expect(Object.fromEntries(api.to(CLEANS)[0].query)).toEqual({
      within_days: "30",
      library_id: "2",
      path_contains: "x",
    });
    expect(screen.getByRole("searchbox", { name: "Find a file" })).toHaveValue(
      "x",
    );
  });

  it("sends no period at all for Everything kept", async () => {
    stubServer();

    renderPage("/activity?within=all");

    await filesLoaded();
    expect(lastListRequest().has("within_days")).toBe(false);
    expect(api.to(CLEANS)[0].query.has("within_days")).toBe(false);
  });

  it("falls back to the last seven days when the address names a period Weir does not offer", async () => {
    stubServer();

    renderPage("/activity?within=90000");

    await filesLoaded();
    expect(lastListRequest().get("within_days")).toBe("7");
  });

  it("opens the library clean the address names, and marks its row as the open one", async () => {
    stubServer();

    renderPage("/activity?clean=5");

    const detail = await screen.findByTestId("activity-detail");
    expect(within(detail).getByRole("heading")).toHaveTextContent("Heat.mkv");
    expect(detail).toHaveTextContent(
      "Cleaned Heat.mkv: removed 1 audio track.",
    );
    const row = screen.getByTitle("Heat (1995)/Heat.mkv").closest("tr");
    expect(row).toHaveAttribute("aria-current", "true");
  });

  it("opens the download the address names, whichever is newest", async () => {
    stubServer();

    renderPage("/activity?file=1");

    const detail = await screen.findByTestId("activity-detail");
    expect(within(detail).getByRole("heading")).toHaveTextContent("alpha.mkv");
    expect(
      screen.getByTitle("Alpha Station/alpha.mkv").closest("tr"),
    ).toHaveAttribute("aria-current", "true");
  });

  it("lists only the group the address names", async () => {
    stubServer();

    renderPage("/activity?show=failed");

    await filesLoaded();
    expect(listedPaths()).toEqual(["Zed Coast/zed.mkv"]);
  });

  it("writes a search into the address and asks again with it", async () => {
    stubServer();
    renderPage();
    await filesLoaded();

    const search = screen.getByRole("searchbox", { name: "Find a file" });
    fireEvent.change(search, { target: { value: "alpha" } });
    fireEvent.submit(search.closest("form")!);

    await vi.waitFor(() =>
      expect(lastListRequest().get("path_contains")).toBe("alpha"),
    );
    expect(api.to(CLEANS).at(-1)!.query.get("path_contains")).toBe("alpha");
  });
});

describe("going back to every kind of file", () => {
  it("takes the kind out of the address when All is chosen again", async () => {
    stubServer();
    renderPage("/activity?show=failed");
    await filesLoaded();
    const show = screen.getByRole("group", { name: "Show" });
    expect(
      within(show).getByRole("button", { name: /^Failed/ }),
    ).toHaveAttribute("aria-pressed", "true");

    fireEvent.click(within(show).getByRole("button", { name: /^All/ }));

    expect(within(show).getByRole("button", { name: /^All/ })).toHaveAttribute(
      "aria-pressed",
      "true",
    );
    expect(
      within(show).getByRole("button", { name: /^Failed/ }),
    ).toHaveAttribute("aria-pressed", "false");
  });
});

describe("sorting Activity by a heading", () => {
  it("lists newest first and says so on the When heading until another heading is chosen", async () => {
    stubServer();

    renderPage();

    await filesLoaded();
    expect(listedPaths()).toEqual([
      "Zed Coast/zed.mkv",
      "Heat (1995)/Heat.mkv",
      "Alpha Station/alpha.mkv",
    ]);
    expect(heading("When")).toHaveAttribute("aria-sort", "descending");
    expect(heading("File")).not.toHaveAttribute("aria-sort");
  });

  it("sorts by File from the first click, asks the server for it, and reverses on the second click", async () => {
    stubServer();
    renderPage();
    await filesLoaded();

    fireEvent.click(sortButton("File"));

    await vi.waitFor(() => expect(lastListRequest().get("sort")).toBe("file"));
    expect(lastListRequest().get("direction")).toBe("asc");
    expect(heading("File")).toHaveAttribute("aria-sort", "ascending");
    expect(heading("When")).not.toHaveAttribute("aria-sort");
    expect(listedPaths()).toEqual([
      "Alpha Station/alpha.mkv",
      "Heat (1995)/Heat.mkv",
      "Zed Coast/zed.mkv",
    ]);

    fireEvent.click(sortButton("File"));

    await vi.waitFor(() =>
      expect(lastListRequest().get("direction")).toBe("desc"),
    );
    expect(heading("File")).toHaveAttribute("aria-sort", "descending");
    expect(listedPaths()).toEqual([
      "Zed Coast/zed.mkv",
      "Heat (1995)/Heat.mkv",
      "Alpha Station/alpha.mkv",
    ]);
  });

  it("sorts by When oldest first on the click after newest first, and back", async () => {
    stubServer();
    renderPage();
    await filesLoaded();

    fireEvent.click(sortButton("When"));

    await vi.waitFor(() =>
      expect(lastListRequest().get("direction")).toBe("asc"),
    );
    expect(lastListRequest().get("sort")).toBe("when");
    expect(heading("When")).toHaveAttribute("aria-sort", "ascending");
    expect(listedPaths()).toEqual([
      "Alpha Station/alpha.mkv",
      "Heat (1995)/Heat.mkv",
      "Zed Coast/zed.mkv",
    ]);

    fireEvent.click(sortButton("When"));

    await vi.waitFor(() =>
      expect(lastListRequest().get("direction")).toBe("desc"),
    );
    expect(heading("When")).toHaveAttribute("aria-sort", "descending");
  });

  it("sorts by What happened in the order of what each means, done before broken, and reverses", async () => {
    stubServer();
    renderPage();
    await filesLoaded();

    fireEvent.click(sortButton("What happened"));

    await vi.waitFor(() =>
      expect(lastListRequest().get("sort")).toBe("status"),
    );
    expect(lastListRequest().get("direction")).toBe("asc");
    expect(heading("What happened")).toHaveAttribute("aria-sort", "ascending");
    expect(listedPaths()).toEqual([
      "Heat (1995)/Heat.mkv",
      "Alpha Station/alpha.mkv",
      "Zed Coast/zed.mkv",
    ]);

    fireEvent.click(sortButton("What happened"));

    await vi.waitFor(() =>
      expect(lastListRequest().get("direction")).toBe("desc"),
    );
    expect(listedPaths()).toEqual([
      "Zed Coast/zed.mkv",
      "Heat (1995)/Heat.mkv",
      "Alpha Station/alpha.mkv",
    ]);
  });
});

describe("when something Activity reads fails", () => {
  it("lists the downloads and says the library cleans could not be loaded, rather than listing none without a word", async () => {
    stubServer({
      overrides: {
        [CLEANS]: jsonAnswer({ detail: "The database is busy." }, 500),
      },
    });

    renderPage();

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Weir couldn't load your library cleans.",
    );
    expect(listedPaths()).toEqual([
      "Zed Coast/zed.mkv",
      "Alpha Station/alpha.mkv",
    ]);
  });

  it("says the workflows could not be loaded when the read of them fails", async () => {
    stubServer({
      overrides: {
        [WORKFLOWS]: jsonAnswer({ detail: "The database is busy." }, 500),
      },
    });

    renderPage();

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Weir couldn't load your workflows.",
    );
    expect(filesTable()).toBeInTheDocument();
  });

  it("says Weir cannot be reached, in the shared words, when the server does not answer the library cleans", async () => {
    stubServer({
      overrides: {
        [CLEANS]: () => {
          throw new TypeError("Failed to fetch");
        },
      },
    });

    renderPage();

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Can't reach Weir",
    );
  });

  it("says the files that need a person could not be read, and never that none is waiting", async () => {
    stubServer({
      overrides: {
        [FILES]: (request: { query: URLSearchParams }) => {
          if (request.query.has("file_status")) {
            return jsonAnswer({ detail: "The database is busy." }, 500);
          }
          return filesPage([ALPHA]);
        },
      },
    });

    renderPage("/activity?show=attention");

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Weir couldn't load the files that need you.",
    );
    expect(screen.queryByText("No file is waiting on you.")).toBeNull();
  });

  it("says nothing about the Needs you read in the other views", async () => {
    stubServer({
      overrides: {
        [FILES]: (request: { query: URLSearchParams }) =>
          request.query.has("file_status")
            ? jsonAnswer({ detail: "The database is busy." }, 500)
            : filesPage([ALPHA]),
      },
    });

    renderPage();

    await filesLoaded();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("reads no problem when every read works", async () => {
    stubServer();

    renderPage("/activity?show=attention");

    expect(
      await screen.findByText("No file is waiting on you."),
    ).toBeInTheDocument();
    expect(screen.queryByRole("alert")).toBeNull();
  });
});
