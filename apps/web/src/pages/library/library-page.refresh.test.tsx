import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { LibraryFile } from "../../lib/processing/library-mode-api";
import {
  cleanQueued,
  confirmationRequired,
  libraryFile,
  libraryFilesPage,
  libraryOverview,
} from "../../test/library-fixtures";
import {
  LIBRARY_ROUTES,
  renderLibraryPage,
  stubLibraryServer,
} from "../../test/library-server";
import {
  jsonAnswer,
  type StubbedApi,
  type StubbedRequest,
} from "../../test/stub-weir-api";

const REDOWNLOAD = "POST /api/v1/processing/libraries/1/library-redownloads";

/** The film in the state the server holds it in, which changes when the page asks for something. */
const film = (state: Partial<LibraryFile> = {}) => libraryFile(state);

let api: StubbedApi;
let held: Partial<LibraryFile>;

beforeEach(() => {
  localStorage.clear();
  held = {};
});

afterEach(() => {
  expect(api.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

function stubServer(routes: Record<string, unknown>) {
  api = stubLibraryServer([film()], {
    [LIBRARY_ROUTES.files]: () => libraryFilesPage([film(held)]),
    [LIBRARY_ROUTES.overview]: () => libraryOverview([film(held)]),
    ...routes,
  });
}

const chip = (name: RegExp) => screen.getByRole("button", { name });
const filmRow = () => screen.getByTestId("library-row");
const reads = () => [
  api.to(LIBRARY_ROUTES.files).length,
  api.to(LIBRARY_ROUTES.overview).length,
];

describe("the table and its counts follow what the server holds, with no reload", () => {
  it("shows a file as Cleaning, and counts it so, once a clean has been queued", async () => {
    stubServer({
      [LIBRARY_ROUTES.clean]: (request: StubbedRequest) => {
        if (
          !(request.body as { confirm_final_removal: boolean })
            .confirm_final_removal
        ) {
          return jsonAnswer(confirmationRequired(1, 1, 333_000_000), 400);
        }
        held = { status: "cleaning", status_reason: "new" };
        return cleanQueued(1);
      },
    });
    renderLibraryPage();
    await screen.findByTestId("library-row");
    expect(chip(/^Needs cleaning\s*1/)).toBeInTheDocument();
    expect(chip(/^Cleaning\s*0/)).toBeInTheDocument();
    expect(filmRow()).toHaveTextContent("1 audio");

    fireEvent.click(within(filmRow()).getByRole("checkbox"));
    fireEvent.click(screen.getByRole("button", { name: "Clean these files" }));
    fireEvent.click(await screen.findByTestId("library-confirm-confirm"));

    await waitFor(() => expect(filmRow()).toHaveTextContent("Cleaning now"));
    expect(chip(/^Cleaning\s*1/)).toBeInTheDocument();
    expect(chip(/^Needs cleaning\s*0/)).toBeInTheDocument();
    expect(reads()).toEqual([2, 2]);
  });

  it("shows a file as Left alone, and counts it so, once it is set aside from its panel", async () => {
    stubServer({
      [LIBRARY_ROUTES.leaveAlone]: (request: StubbedRequest) => {
        held = { leave_alone: true, status: "left_alone" };
        return {
          path: (request.body as { path: string }).path,
          leave_alone: true,
        };
      },
    });
    renderLibraryPage();
    await screen.findByTestId("library-row");
    expect(chip(/^Left alone\s*0/)).toBeInTheDocument();
    fireEvent.click(within(filmRow()).getByRole("button", { name: /Arrival/ }));
    const drawer = await screen.findByTestId("library-file-drawer");

    fireEvent.click(
      within(drawer).getByRole("checkbox", { name: /Left alone/ }),
    );

    await waitFor(() => expect(chip(/^Left alone\s*1/)).toBeInTheDocument());
    expect(chip(/^Needs cleaning\s*0/)).toBeInTheDocument();
    expect(filmRow()).toHaveTextContent("Left alone");
    expect(reads()).toEqual([2, 2]);
  });

  it("reads the table and the counts again once a title is asked for again", async () => {
    stubServer({
      "GET /api/v1/processing/libraries/1/library-redownloads": {
        library_id: 1,
        total: 1,
        titles: [
          {
            path: film().path,
            manager_kind: "radarr",
            manager_title: "Arrival",
            removed_tracks: [
              {
                language: "fre",
                type: "audio",
                codec: "ac3",
                variant: null,
                reason: "Not a language your rules kept at the time",
              },
            ],
            can_redownload: true,
            confirmation_message:
              "Radarr will delete Arrival's current file first.",
            unavailable_reason: null,
            waiting_note: null,
          },
        ],
      },
      [REDOWNLOAD]: {
        path: film().path,
        outcome: "requested",
        message:
          "Radarr on nas deleted the existing file and is searching for a replacement.",
      },
    });
    renderLibraryPage();
    await screen.findByTestId("library-row");
    fireEvent.click(within(filmRow()).getByRole("button", { name: /Arrival/ }));
    const drawer = await screen.findByTestId("library-file-drawer");
    fireEvent.click(
      await within(drawer).findByRole("button", { name: "Download again" }),
    );
    fireEvent.click(
      await screen.findByRole("button", { name: "Yes, download it again" }),
    );

    await within(drawer).findByText(/is searching for a replacement/);

    expect(reads()).toEqual([2, 2]);
  });

  it("reads them again once a check of the library has been started", async () => {
    stubServer({
      [LIBRARY_ROUTES.scan]: {
        already_running: false,
        job_id: 9,
        status: "queued",
      },
    });
    renderLibraryPage();
    await screen.findByTestId("library-row");

    fireEvent.click(screen.getByRole("button", { name: "Check again" }));

    await waitFor(() => expect(reads()).toEqual([2, 2]));
  });
});
