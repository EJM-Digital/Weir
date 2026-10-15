import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { workflowRow } from "../../test/dashboard-fixtures";
import { elapse, useFakeClock, useRealClock } from "../../test/fake-clock";
import {
  libraryFile,
  libraryFilesPage,
  libraryOverview,
  librarySettings,
} from "../../test/library-fixtures";
import {
  LIBRARY_ROUTES,
  renderLibraryPage,
  stubLibraryServer,
} from "../../test/library-server";
import type { StubbedApi } from "../../test/stub-weir-api";

const FILMS = [
  libraryFile({ path: "D:/films/Arrival (2016)/Arrival (2016).mkv" }),
  libraryFile({ path: "D:/films/Heat (1995)/Heat (1995).mkv" }),
];
const SECOND_LIBRARY = "/api/v1/processing/libraries/2";
const OTHER_FILM = libraryFile({
  path: "D:/tv/Northbound/Northbound.S01E01.mkv",
});

let api: StubbedApi;

function stubServer() {
  const everything = [...FILMS];
  api = stubLibraryServer(FILMS, {
    // A library of more files than fit on a page, so there is a second page to turn to.
    [LIBRARY_ROUTES.files]: (request: { query: URLSearchParams }) =>
      libraryFilesPage(everything, {
        total: 450,
        page: Number(request.query.get("page") ?? 1),
      }),
    "GET /api/v1/processing/libraries": [
      workflowRow({ id: 1, name: "Movies" }),
      workflowRow({ id: 2, name: "TV", media_type: "tv" }),
    ],
    [`GET ${SECOND_LIBRARY}/library-files`]: libraryFilesPage([OTHER_FILM], {
      library_id: 2,
    }),
    [`GET ${SECOND_LIBRARY}/library-overview`]: libraryOverview([OTHER_FILM], {
      library_id: 2,
    }),
    [`GET ${SECOND_LIBRARY}/library-settings`]: librarySettings({
      library_folders: ["D:/tv"],
    }),
  });
}

/** The last time the page asked for the files of the first library. */
const lastFilesRequest = () => api.to(LIBRARY_ROUTES.files).at(-1)!.query;

const bulk = () => screen.queryByTestId("library-bulk");

async function selectFirstFile() {
  const rows = await screen.findAllByTestId("library-row");
  fireEvent.click(within(rows[0]).getByRole("checkbox"));
  expect(bulk()).toHaveTextContent("1 selected");
}

beforeEach(() => {
  localStorage.clear();
  stubServer();
});

afterEach(() => {
  useRealClock();
  expect(api.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

describe("a selection of library files belongs to the rows on screen", () => {
  it("is kept while nothing changes the rows", async () => {
    renderLibraryPage();

    await selectFirstFile();

    expect(bulk()).toHaveTextContent("1 selected");
  });

  it("is cleared by choosing a status, which starts again at the first page", async () => {
    renderLibraryPage();
    await selectFirstFile();
    fireEvent.click(screen.getByRole("button", { name: "Next" }));
    await waitFor(() => expect(lastFilesRequest().get("page")).toBe("2"));
    await selectFirstFile();

    fireEvent.click(screen.getByRole("button", { name: /Needs cleaning/ }));

    expect(bulk()).toBeNull();
    await waitFor(() =>
      expect(lastFilesRequest().get("status")).toBe("needs_cleaning"),
    );
    expect(lastFilesRequest().has("page")).toBe(false);
    expect(screen.getByTestId("library-range")).toHaveTextContent(
      "Showing 1–2 of 450",
    );
  });

  it("is cleared by typing in the search, which starts again at the first page", async () => {
    useFakeClock();
    renderLibraryPage();
    await selectFirstFile();
    fireEvent.click(screen.getByRole("button", { name: "Next" }));
    await waitFor(() => expect(lastFilesRequest().get("page")).toBe("2"));
    await selectFirstFile();

    fireEvent.change(
      screen.getByRole("searchbox", { name: "Search this workflow" }),
      { target: { value: "arrival" } },
    );

    expect(bulk()).toBeNull();
    await elapse(300);
    await waitFor(() => expect(lastFilesRequest().get("q")).toBe("arrival"));
    expect(lastFilesRequest().has("page")).toBe(false);
  });

  it("is cleared by sorting by another heading, which starts again at the first page", async () => {
    renderLibraryPage();
    await selectFirstFile();
    fireEvent.click(screen.getByRole("button", { name: "Next" }));
    await waitFor(() => expect(lastFilesRequest().get("page")).toBe("2"));
    await selectFirstFile();

    fireEvent.click(screen.getByRole("button", { name: "Size" }));

    expect(bulk()).toBeNull();
    await waitFor(() => expect(lastFilesRequest().get("sort")).toBe("size"));
    expect(lastFilesRequest().get("direction")).toBe("desc");
    expect(lastFilesRequest().has("page")).toBe(false);
  });

  it("is cleared by turning the page, and the page says so while it is kept", async () => {
    renderLibraryPage();
    await selectFirstFile();
    expect(screen.getByTestId("library-pager")).toHaveTextContent(
      "Your selection is for this page: moving to another page clears it.",
    );

    fireEvent.click(screen.getByRole("button", { name: "Next" }));

    expect(bulk()).toBeNull();
    await waitFor(() => expect(lastFilesRequest().get("page")).toBe("2"));
    expect(screen.getByTestId("library-pager")).not.toHaveTextContent(
      "Your selection is for this page",
    );
  });

  it("is cleared by moving to another workflow, whose files start at the first page", async () => {
    renderLibraryPage();
    await selectFirstFile();

    fireEvent.click(
      within(screen.getByTestId("library-picker")).getByRole("button", {
        name: /Movies/,
      }),
    );
    fireEvent.click(await screen.findByRole("option", { name: /TV/ }));

    expect(bulk()).toBeNull();
    await screen.findByText("Northbound.S01E01.mkv");
    expect(api.to(`GET ${SECOND_LIBRARY}/library-files`)).toHaveLength(1);
    expect(screen.getByTestId("address")).toHaveTextContent("?library=2");
  });
});
