import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { libraryFile, librarySettings } from "../../test/library-fixtures";
import {
  LIBRARY_ID,
  LIBRARY_ROUTES,
  renderLibraryPage,
  stubLibraryServer,
} from "../../test/library-server";
import { savedFields, type StubbedApi } from "../../test/stub-weir-api";

const PUT_SETTINGS = `PUT /api/v1/processing/libraries/${LIBRARY_ID}/library-settings`;
const DIRECTORIES = "GET /api/v1/system/directories";
const FILMS = "D:/films";

let api: StubbedApi;

/** The Library page for a workflow whose setup holds `folders`, with its setup panel open. */
async function openSetup(folders: string[]) {
  api = stubLibraryServer([libraryFile()], {
    "GET /api/v1/processing/rule-sets": [],
    [LIBRARY_ROUTES.settings]: librarySettings({ library_folders: folders }),
    [PUT_SETTINGS]: librarySettings({ library_folders: folders }),
    [DIRECTORIES]: {
      current_path: null,
      parent_path: null,
      entries: [{ name: "E:", path: "E:\\", kind: "root", description: null }],
    },
  });
  renderLibraryPage();
  fireEvent.click(
    await screen.findByRole("button", {
      name: folders.length === 0 ? "Set up this library" : "Library setup",
    }),
  );
  await screen.findByTestId("library-setup-panel");
  // The panel is read-only until the session says the person may change it.
  await screen.findByRole("textbox", { name: "Folder to add" });
}

const folderInput = () =>
  screen.getByRole("textbox", { name: "Folder to add" });
const listedFolders = () =>
  within(screen.getByRole("list", { name: "Library folders" }))
    .queryAllByRole("listitem")
    .map((item) => item.textContent);
const save = () => screen.getByTestId("library-setup-save");

beforeEach(() => {
  localStorage.clear();
});

afterEach(() => {
  expect(api.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

describe("adding a folder to a workflow's library", () => {
  it("adds the typed folder when Enter is pressed, and saves it with the others on Save", async () => {
    await openSetup([FILMS]);

    fireEvent.change(folderInput(), { target: { value: " D:/more " } });
    fireEvent.keyDown(folderInput(), { key: "Enter" });

    expect(listedFolders()).toEqual([
      expect.stringContaining(FILMS),
      expect.stringContaining("D:/more"),
    ]);
    expect(folderInput()).toHaveValue("");
    fireEvent.click(save());
    await waitFor(() => expect(api.to(PUT_SETTINGS)).toHaveLength(1));
    expect(savedFields(api.to(PUT_SETTINGS)[0])).toMatchObject({
      library_folders: [FILMS, "D:/more"],
    });
  });

  it("adds the folder chosen in the folder picker", async () => {
    await openSetup([FILMS]);

    fireEvent.click(screen.getByRole("button", { name: "Browse" }));
    fireEvent.click(await screen.findByRole("button", { name: "Select" }));

    await waitFor(() => expect(listedFolders()).toHaveLength(2));
    expect(listedFolders()[1]).toContain("E:\\");
    expect(api.to(DIRECTORIES)).toHaveLength(1);
  });

  it("ignores a blank folder: Add folder is off, Enter adds nothing, and there is nothing to save", async () => {
    await openSetup([FILMS]);
    const add = screen.getByRole("button", { name: "Add folder" });
    expect(add).toBeDisabled();

    fireEvent.change(folderInput(), { target: { value: "   " } });
    fireEvent.keyDown(folderInput(), { key: "Enter" });

    expect(add).toBeDisabled();
    expect(listedFolders()).toHaveLength(1);
    expect(save()).toBeDisabled();
  });

  it("ignores a folder the library already holds, so it is listed once and there is nothing to save", async () => {
    await openSetup([FILMS]);

    fireEvent.change(folderInput(), { target: { value: FILMS } });
    fireEvent.click(screen.getByRole("button", { name: "Add folder" }));

    expect(listedFolders()).toHaveLength(1);
    expect(save()).toBeDisabled();
  });
});

describe("the daily clean of a library with no folder", () => {
  const dailyClean = () =>
    screen.getByRole("radiogroup", { name: /Check and clean once a day/ });

  it("cannot be switched on until the library has a folder", async () => {
    await openSetup([]);

    for (const radio of within(dailyClean()).getAllByRole("radio")) {
      expect(radio).toBeDisabled();
    }

    fireEvent.change(folderInput(), { target: { value: FILMS } });
    fireEvent.click(screen.getByRole("button", { name: "Add folder" }));

    for (const radio of within(dailyClean()).getAllByRole("radio")) {
      expect(radio).toBeEnabled();
    }
  });

  it("is switched off again by taking the last folder away", async () => {
    await openSetup([FILMS]);
    expect(
      within(dailyClean()).getByRole("radio", { name: "On" }),
    ).toBeEnabled();

    fireEvent.click(screen.getByRole("button", { name: `Remove ${FILMS}` }));

    expect(
      within(dailyClean()).getByRole("radio", { name: "On" }),
    ).toBeDisabled();
  });
});
