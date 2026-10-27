import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { setupTabPath } from "../../lib/settings/setup-areas";
import { activityPage, passFinished } from "../../test/dashboard-fixtures";
import {
  libraryFile,
  previewOfTwoAudioTracks,
} from "../../test/library-fixtures";
import {
  LIBRARY_ID,
  LIBRARY_ROUTES,
  renderLibraryPage,
  stubLibraryServer,
} from "../../test/library-server";
import { unexpectedProblem, type StubbedApi } from "../../test/stub-weir-api";

const ARRIVAL = libraryFile();
const HEAT = libraryFile({
  path: "D:/films/Heat (1995)/Heat (1995).mkv",
  summary: "Would remove 1 audio track",
});
const ACTIVITY = "GET /api/v1/activity/recent";

let api: StubbedApi;
const never = () => new Promise<never>(() => undefined);

/** The boxes that choose a track, not the one that leaves the file alone. */
const trackBoxes = (drawer: HTMLElement) =>
  drawer.querySelectorAll(".mm-drawer__track input[type=checkbox]");

async function openFile(name: RegExp = /Arrival/) {
  const rows = await screen.findAllByTestId("library-row");
  const row = rows.find((r) => name.test(r.textContent ?? ""))!;
  fireEvent.click(within(row).getByRole("button", { name }));
  return screen.findByTestId("library-file-drawer");
}

beforeEach(() => {
  localStorage.clear();
});

afterEach(() => {
  expect(api.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

describe("the tracks in the file panel", () => {
  it("says it is reading the file's tracks until the server has answered", async () => {
    api = stubLibraryServer([ARRIVAL], { [LIBRARY_ROUTES.preview]: never });
    renderLibraryPage();

    const drawer = await openFile();

    expect(
      await within(drawer).findByText("Reading this file’s tracks…"),
    ).toBeVisible();
    expect(within(drawer).queryByText(/Audio · /)).toBeNull();
  });

  it("lists a track the rules gave no reason for with its Keep or Remove and nothing beside it", async () => {
    const preview = previewOfTwoAudioTracks();
    api = stubLibraryServer([ARRIVAL], {
      [LIBRARY_ROUTES.preview]: {
        ...preview,
        tracks: preview.tracks.map((track) => ({ ...track, reasons: [] })),
      },
    });
    renderLibraryPage();

    const drawer = await openFile();

    expect(await within(drawer).findByText("Keep")).toBeVisible();
    expect(within(drawer).getByText("Remove")).toBeVisible();
    expect(drawer.querySelectorAll(".mm-drawer__track-why")).toHaveLength(2);
    for (const why of drawer.querySelectorAll(".mm-drawer__track-why")) {
      expect(why).toBeEmptyDOMElement();
    }
  });

  it("says Weir could not read the file just now, and lists no tracks, when the server fails", async () => {
    api = stubLibraryServer([ARRIVAL], {
      [LIBRARY_ROUTES.preview]: unexpectedProblem(),
    });
    renderLibraryPage();

    const drawer = await openFile();

    expect(
      await within(drawer).findByText(
        /Weir could not read this file just now:/,
      ),
    ).toBeVisible();
    expect(within(drawer).queryByText(/Audio · /)).toBeNull();
  });
});

describe("choosing the tracks and going back to the rules", () => {
  it("hands the choice back to the workflow's rules, which show again with their reasons", async () => {
    api = stubLibraryServer([ARRIVAL]);
    renderLibraryPage();
    const drawer = await openFile();
    fireEvent.click(
      await within(drawer).findByRole("button", { name: "Choose tracks" }),
    );
    expect(trackBoxes(drawer)).toHaveLength(2);
    expect(
      within(drawer).getByText("Your choice, for this file only."),
    ).toBeVisible();

    fireEvent.click(
      within(drawer).getByRole("button", { name: "Use this workflow’s rules" }),
    );

    expect(trackBoxes(drawer)).toHaveLength(0);
    expect(
      within(drawer).getByText(/Decided by this workflow’s rules\./),
    ).toBeVisible();
    expect(
      within(drawer).getByRole("button", { name: "Choose tracks" }),
    ).toBeEnabled();
    expect(
      within(drawer).getByText("Your first choice language"),
    ).toBeVisible();
  });

  it("counts a subtitle track in and out of the choice as its box is ticked", async () => {
    const preview = previewOfTwoAudioTracks();
    api = stubLibraryServer([ARRIVAL], {
      [LIBRARY_ROUTES.preview]: {
        ...preview,
        tracks: [
          ...preview.tracks,
          {
            index: 3,
            type: "subtitle",
            codec: "subrip",
            language: "English",
            channels: null,
            action: "keep",
            reasons: ["Your first choice language"],
            default: false,
            forced: false,
            title: "",
          },
        ],
      },
    });
    renderLibraryPage();
    const drawer = await openFile();
    fireEvent.click(
      await within(drawer).findByRole("button", { name: "Choose tracks" }),
    );
    expect(
      within(drawer).getByText(/Keeping 2 of 3 tracks; 1 would come out/),
    ).toBeVisible();

    fireEvent.click(trackBoxes(drawer)[2]);

    expect(
      within(drawer).getByText(/Keeping 1 of 3 tracks; 2 would come out/),
    ).toBeVisible();

    fireEvent.click(trackBoxes(drawer)[2]);

    expect(
      within(drawer).getByText(/Keeping 2 of 3 tracks; 1 would come out/),
    ).toBeVisible();
  });

  it("starts the track choice again when another file is opened", async () => {
    api = stubLibraryServer([ARRIVAL, HEAT]);
    renderLibraryPage();
    const drawer = await openFile(/Arrival/);
    fireEvent.click(
      await within(drawer).findByRole("button", { name: "Choose tracks" }),
    );
    expect(trackBoxes(drawer)).toHaveLength(2);

    const second = await openFile(/Heat/);

    await within(second).findByRole("button", { name: "Choose tracks" });
    expect(trackBoxes(second)).toHaveLength(0);
    expect(second).toHaveTextContent("Heat (1995).mkv");
  });
});

describe("checking the file again", () => {
  it("reads the tracks again and says Checking… while it does", async () => {
    let reads = 0;
    let finish: () => void = () => undefined;
    const second = new Promise<void>((resolve) => (finish = resolve));
    api = stubLibraryServer([ARRIVAL], {
      [LIBRARY_ROUTES.preview]: async () => {
        reads += 1;
        if (reads > 1) await second;
        return previewOfTwoAudioTracks();
      },
    });
    renderLibraryPage();
    const drawer = await openFile();
    await within(drawer).findByRole("button", { name: "Check it again" });

    fireEvent.click(
      within(drawer).getByRole("button", { name: "Check it again" }),
    );

    expect(
      await within(drawer).findByRole("button", { name: "Checking…" }),
    ).toBeDisabled();
    expect(api.to(LIBRARY_ROUTES.preview)).toHaveLength(2);
    finish();
    expect(
      await within(drawer).findByRole("button", { name: "Check it again" }),
    ).toBeEnabled();
  });
});

describe("the rules link", () => {
  it("opens the profiles in Setup for this library", async () => {
    api = stubLibraryServer([ARRIVAL]);
    renderLibraryPage();
    const drawer = await openFile();

    const link = await within(drawer).findByRole("link", {
      name: "Edit the rules →",
    });

    expect(link).toHaveAttribute(
      "href",
      `${setupTabPath("profiles")}?library=${LIBRARY_ID}`,
    );
  });
});

describe("what has happened to the file", () => {
  it("lists what Weir recorded for this file, asked for by its path, newest first as the server sent them", async () => {
    api = stubLibraryServer([ARRIVAL], {
      [ACTIVITY]: activityPage([
        passFinished(12, ARRIVAL.path, {
          createdAt: "2026-10-15T10:00:00+00:00",
        }),
        passFinished(11, ARRIVAL.path, {
          createdAt: "2026-10-14T10:00:00+00:00",
        }),
      ]),
    });
    renderLibraryPage();
    const drawer = await openFile();

    expect(
      await within(drawer).findByText("What has happened to this file"),
    ).toBeVisible();

    const steps = drawer.querySelectorAll(".mm-drawer__step");
    expect(steps).toHaveLength(2);
    expect(steps[0]).toHaveTextContent(`Remux finished for ${ARRIVAL.path}`);
    await waitFor(() => {
      const asked = api
        .to(ACTIVITY)
        .find((request) => request.query.get("file") === ARRIVAL.path);
      expect(asked?.query.get("limit")).toBe("6");
    });
  });

  it("shows no history heading for a file nothing has happened to", async () => {
    api = stubLibraryServer([ARRIVAL]);
    renderLibraryPage();
    const drawer = await openFile();
    await within(drawer).findByRole("button", { name: "Choose tracks" });

    expect(
      within(drawer).queryByText("What has happened to this file"),
    ).toBeNull();
  });
});
