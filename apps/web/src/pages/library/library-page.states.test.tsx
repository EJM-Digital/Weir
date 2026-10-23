import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { elapse, useFakeClock, useRealClock } from "../../test/fake-clock";
import { workflowRow } from "../../test/dashboard-fixtures";
import {
  libraryFile,
  libraryFilesPage,
  libraryOverview,
  librarySettings,
} from "../../test/library-fixtures";
import {
  LIBRARY_ID,
  LIBRARY_ROUTES,
  renderLibraryPage,
  stubLibraryServer,
} from "../../test/library-server";
import {
  unexpectedProblem,
  savedFields,
  type StubbedApi,
} from "../../test/stub-weir-api";

const LIBRARIES = "GET /api/v1/processing/libraries";
const PUT_SETTINGS = `PUT /api/v1/processing/libraries/${LIBRARY_ID}/library-settings`;
const ARRIVAL = libraryFile();
const HEAT = libraryFile({ path: "D:/films/Heat (1995)/Heat (1995).mkv" });

let api: StubbedApi;
const never = () => new Promise<never>(() => undefined);
const address = () => screen.getByTestId("address").textContent ?? "";

beforeEach(() => {
  localStorage.clear();
});

afterEach(() => {
  useRealClock();
  expect(api.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

describe("before the Library page has its workflows", () => {
  it("says it is loading your workflows while the list has not arrived", async () => {
    api = stubLibraryServer([ARRIVAL], { [LIBRARIES]: never });

    renderLibraryPage();

    expect(await screen.findByText("Loading your workflows")).toBeVisible();
    expect(screen.queryByTestId("library-page")).toBeNull();
  });

  it("says Weir hit a problem, and offers a reload, when the workflows cannot be read", async () => {
    vi.stubEnv("DEV", false);
    api = stubLibraryServer([ARRIVAL], { [LIBRARIES]: unexpectedProblem() });

    renderLibraryPage();

    expect(await screen.findByText("Weir hit a problem")).toBeVisible();
    expect(screen.getByRole("button", { name: "Reload" })).toBeVisible();
    expect(screen.queryByTestId("library-page")).toBeNull();
    vi.unstubAllEnvs();
  });

  it("points to Setup > Workflows when no workflow is set up", async () => {
    api = stubLibraryServer([], { [LIBRARIES]: [] });

    renderLibraryPage();

    expect(await screen.findByTestId("library-page")).toHaveTextContent(
      "No workflow is set up yet. Add one in Setup › Workflows, then come back here and tell Weir which folders the files you already have sit in.",
    );
    expect(screen.queryByTestId("library-controls")).toBeNull();
  });
});

describe("a workflow whose folders hold no files Weir reads", () => {
  it("says it has not been scanned yet, or its folders hold nothing, and where to change them", async () => {
    api = stubLibraryServer([]);

    renderLibraryPage();

    expect(await screen.findByTestId("library-empty")).toHaveTextContent(
      "This workflow has not been scanned yet, or its folders hold nothing Weir reads. Check again, or change its folders in Library setup.",
    );
  });
});

describe("while the overview of the workflow is still being read", () => {
  it("lists the files without the totals it has not heard yet", async () => {
    api = stubLibraryServer([ARRIVAL], {
      [LIBRARY_ROUTES.overview]: never,
    });

    renderLibraryPage();

    expect(await screen.findAllByTestId("library-row")).toHaveLength(1);
  });

  it("lists the workflow in the picker without a count it has not heard yet", async () => {
    api = stubLibraryServer([ARRIVAL], {
      [LIBRARIES]: [
        workflowRow({ id: LIBRARY_ID, name: "Movies" }),
        workflowRow({ id: 2, name: "TV" }),
      ],
      [LIBRARY_ROUTES.overview]: never,
    });
    renderLibraryPage();
    await screen.findAllByTestId("library-row");

    fireEvent.click(
      within(screen.getByTestId("library-picker")).getByRole("button", {
        name: /Movies/,
      }),
    );

    expect(screen.getByRole("option", { name: /TV/ })).toBeVisible();
  });
});

describe("while the files of the workflow are read", () => {
  it("says it is reading the workflow until the files arrive", async () => {
    api = stubLibraryServer([ARRIVAL], { [LIBRARY_ROUTES.files]: never });

    renderLibraryPage();

    expect(await screen.findByText("Reading this workflow")).toBeVisible();
    expect(screen.queryByTestId("library-row")).toBeNull();
  });

  it("says Weir hit a problem, and offers a reload, when the files cannot be read", async () => {
    vi.stubEnv("DEV", false);
    api = stubLibraryServer([ARRIVAL], {
      [LIBRARY_ROUTES.files]: unexpectedProblem(),
    });

    renderLibraryPage();

    expect(await screen.findByText("Weir hit a problem")).toBeVisible();
    expect(screen.getByRole("button", { name: "Reload" })).toBeVisible();
    expect(screen.queryByTestId("library-empty")).toBeNull();
    vi.unstubAllEnvs();
  });
});

describe("what the address holds", () => {
  const chips = () => screen.getByRole("group", { name: "Show" });

  it("opens the search and the file the address names, once", async () => {
    api = stubLibraryServer([ARRIVAL, HEAT]);

    renderLibraryPage(
      `/library?library=${LIBRARY_ID}&q=Arrival&path=${encodeURIComponent(ARRIVAL.path)}`,
    );

    expect(
      await screen.findByRole("searchbox", { name: "Search this workflow" }),
    ).toHaveValue("Arrival");
    expect(await screen.findByTestId("library-file-drawer")).toBeVisible();
    await waitFor(() =>
      expect(api.to(LIBRARY_ROUTES.files).at(-1)!.query.get("q")).toBe(
        "Arrival",
      ),
    );
  });

  it("holds only the workflow and the status chosen: the search, the page, the sort and the open file are not written to it", async () => {
    useFakeClock();
    api = stubLibraryServer([ARRIVAL, HEAT]);
    renderLibraryPage();
    const rows = await screen.findAllByTestId("library-row");

    fireEvent.change(
      screen.getByRole("searchbox", { name: "Search this workflow" }),
      { target: { value: "heat" } },
    );
    await elapse(300);
    fireEvent.click(within(rows[0]).getByRole("button", { name: /Arrival/ }));
    fireEvent.click(screen.getByRole("button", { name: "Size" }));
    fireEvent.click(
      within(chips()).getByRole("button", { name: /Needs cleaning/ }),
    );

    await waitFor(() =>
      expect(new URLSearchParams(address()).get("show")).toBe("needs_cleaning"),
    );
    expect([...new URLSearchParams(address()).keys()].sort()).toEqual([
      "library",
      "show",
    ]);
  });

  it("drops the search and the file a link named from the address once the person goes on, so a reload does not bring them back", async () => {
    api = stubLibraryServer([ARRIVAL, HEAT]);
    renderLibraryPage(
      `/library?library=${LIBRARY_ID}&q=Arrival&path=${encodeURIComponent(ARRIVAL.path)}`,
    );
    await screen.findByTestId("library-file-drawer");

    fireEvent.click(
      within(chips()).getByRole("button", { name: /Needs cleaning/ }),
    );

    await waitFor(() => expect(address()).toContain("show=needs_cleaning"));
    expect(new URLSearchParams(address()).has("q")).toBe(false);
    expect(new URLSearchParams(address()).has("path")).toBe(false);
    expect(new URLSearchParams(address()).get("library")).toBe(
      String(LIBRARY_ID),
    );
  });
});

describe("saving the setup of a workflow", () => {
  /** A server holding the library's setup: what is saved is what the next read returns. */
  function serveSetup(saved: { library_folders: string[] }) {
    let stored = librarySettings(saved);
    let files = saved.library_folders.length === 0 ? [] : [ARRIVAL];
    api = stubLibraryServer([ARRIVAL], {
      "GET /api/v1/processing/rule-sets": [],
      [LIBRARY_ROUTES.settings]: () => stored,
      [LIBRARY_ROUTES.files]: () => libraryFilesPage(files),
      [LIBRARY_ROUTES.overview]: () =>
        libraryOverview(files, {
          folders_configured: stored.library_folders.length,
        }),
      [PUT_SETTINGS]: (request: { body: unknown }) => {
        stored = { ...stored, ...savedFields(request as never) };
        files = [ARRIVAL];
        return stored;
      },
    });
  }

  it("shows the new folders when the setup is opened again, and reads the files and the counts again", async () => {
    serveSetup({ library_folders: ["D:/films"] });
    renderLibraryPage();
    await screen.findAllByTestId("library-row");
    fireEvent.click(screen.getByRole("button", { name: "Library setup" }));
    fireEvent.change(
      await screen.findByRole("textbox", { name: "Folder to add" }),
      {
        target: { value: "D:/more-films" },
      },
    );
    fireEvent.click(screen.getByRole("button", { name: "Add folder" }));
    const filesReads = api.to(LIBRARY_ROUTES.files).length;
    const overviewReads = api.to(LIBRARY_ROUTES.overview).length;

    fireEvent.click(screen.getByTestId("library-setup-save"));

    await waitFor(() =>
      expect(screen.queryByTestId("library-setup-panel")).toBeNull(),
    );
    expect(savedFields(api.to(PUT_SETTINGS)[0])).toMatchObject({
      library_folders: ["D:/films", "D:/more-films"],
    });
    await waitFor(() =>
      expect(api.to(LIBRARY_ROUTES.files).length).toBeGreaterThan(filesReads),
    );
    await waitFor(() =>
      expect(api.to(LIBRARY_ROUTES.overview).length).toBeGreaterThan(
        overviewReads,
      ),
    );
    fireEvent.click(screen.getByRole("button", { name: "Library setup" }));
    const folders = await screen.findByRole("list", {
      name: "Library folders",
    });
    expect(folders).toHaveTextContent("D:/more-films");
  });

  it("leaves the prompt to set up a workflow once it has gained a folder", async () => {
    serveSetup({ library_folders: [] });
    renderLibraryPage();
    fireEvent.click(
      await screen.findByRole("button", { name: "Set up this library" }),
    );
    fireEvent.change(
      await screen.findByRole("textbox", { name: "Folder to add" }),
      {
        target: { value: "D:/films" },
      },
    );
    fireEvent.click(screen.getByRole("button", { name: "Add folder" }));

    fireEvent.click(screen.getByTestId("library-setup-save"));

    await waitFor(() =>
      expect(screen.queryByTestId("library-setup-prompt")).toBeNull(),
    );
    expect(await screen.findAllByTestId("library-row")).toHaveLength(1);
  });
});
