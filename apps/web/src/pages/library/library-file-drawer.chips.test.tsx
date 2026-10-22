import { fireEvent, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { libraryFile } from "../../test/library-fixtures";
import {
  renderLibraryPage,
  stubLibraryServer,
} from "../../test/library-server";
import type { StubbedApi } from "../../test/stub-weir-api";

let api: StubbedApi;

async function openOnly(file: ReturnType<typeof libraryFile>) {
  api = stubLibraryServer([file]);
  renderLibraryPage();
  const row = await screen.findByTestId("library-row");
  fireEvent.click(within(row).getByRole("button", { name: /Arrival/ }));
  return screen.findByTestId("library-file-drawer");
}

beforeEach(() => {
  localStorage.clear();
});

afterEach(() => {
  expect(api.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

describe("what the file panel says about a file", () => {
  it("shows the picture size, the codec and the manager that matched it", async () => {
    const drawer = await openOnly(libraryFile({ manager_kind: "radarr" }));

    expect(within(drawer).getByText("1080p")).toBeVisible();
    expect(within(drawer).getByText("Radarr: matched")).toBeVisible();
  });

  it("names a manager Weir has no name for as the server sent it", async () => {
    const drawer = await openOnly(libraryFile({ manager_kind: "lidarr" }));

    expect(within(drawer).getByText("lidarr: matched")).toBeVisible();
  });

  it("leaves out the picture size and the codec when the file does not report them", async () => {
    const drawer = await openOnly(
      libraryFile({ video_height: null, video_codec: "unknown" }),
    );

    expect(within(drawer).queryByText(/\dp$/)).toBeNull();
    expect(within(drawer).queryByText(/unknown/i)).toBeNull();
  });

  it("gives the reason, or says the file has not been judged, when there is no summary", async () => {
    const drawer = await openOnly(
      libraryFile({ summary: null, reason: "Its folder is not watched." }),
    );
    expect(
      within(drawer).getByText("Its folder is not watched."),
    ).toBeVisible();
  });

  it("says the file has not been judged yet when it has neither summary nor reason", async () => {
    const drawer = await openOnly(libraryFile({ summary: null, reason: null }));

    expect(
      within(drawer).getByText("Weir has not judged this file yet."),
    ).toBeVisible();
  });
});
