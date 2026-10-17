import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
  cleanQueued,
  confirmationRequired,
  libraryFile,
  libraryFilesPage,
  libraryOverview,
  matchingFile,
} from "../../test/library-fixtures";
import {
  LIBRARY_ROUTES,
  renderLibraryPage,
  stubLibraryServer,
} from "../../test/library-server";
import {
  jsonAnswer,
  unexpectedProblem,
  type StubbedApi,
  type StubbedRequest,
} from "../../test/stub-weir-api";

const FILM = libraryFile();
const FILM_PATH = FILM.path;
const START_FAILED =
  "Weir couldn't start the clean. Nothing was changed. Try again.";
const SAVE_FAILED =
  "Weir couldn't save that. The file is as it was; try again.";

let api: StubbedApi;

beforeEach(() => {
  localStorage.clear();
});

afterEach(() => {
  expect(api.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

async function openTheFilm() {
  const row = await screen.findByTestId("library-row");
  fireEvent.click(within(row).getByRole("button", { name: /Arrival/ }));
  return screen.findByTestId("library-file-drawer");
}

const cleanButton = (drawer: HTMLElement) =>
  within(drawer).getByRole("button", {
    name: /^(Clean this file|Checking this file…)$/,
  });

describe("cleaning one file from its panel", () => {
  it("says Checking this file… and holds the button while the server works out what the clean would remove", async () => {
    let answer: () => void = () => undefined;
    const answered = new Promise<void>((resolve) => (answer = resolve));
    api = stubLibraryServer([FILM], {
      [LIBRARY_ROUTES.clean]: async () => {
        await answered;
        return jsonAnswer(confirmationRequired(1, 1, 333_000_000), 400);
      },
    });
    renderLibraryPage();
    const drawer = await openTheFilm();
    await within(drawer).findByText(/Audio · 2 tracks/);

    fireEvent.click(cleanButton(drawer));

    const checking = await within(drawer).findByRole("button", {
      name: "Checking this file…",
    });
    expect(checking).toBeDisabled();
    answer();
    expect(await screen.findByTestId("library-confirm")).toHaveTextContent(
      "Clean this file?",
    );
    expect(cleanButton(drawer)).toHaveTextContent("Clean this file");
  });

  it("sends the file, first to ask and then confirmed, and shows what was queued in the panel", async () => {
    api = stubLibraryServer([FILM], {
      [LIBRARY_ROUTES.clean]: (request: StubbedRequest) =>
        (request.body as { confirm_final_removal: boolean })
          .confirm_final_removal
          ? cleanQueued(1)
          : jsonAnswer(confirmationRequired(1, 1, 333_000_000), 400),
    });
    renderLibraryPage();
    const drawer = await openTheFilm();
    await within(drawer).findByText(/Audio · 2 tracks/);

    fireEvent.click(cleanButton(drawer));
    fireEvent.click(await screen.findByTestId("library-confirm-confirm"));

    const outcome = await within(drawer).findByTestId("library-outcome");
    expect(outcome).toHaveTextContent("1 file is queued to clean.");
    expect(api.to(LIBRARY_ROUTES.clean).map((r) => r.body)).toEqual([
      expect.objectContaining({
        paths: [FILM_PATH],
        confirm_final_removal: false,
      }),
      expect.objectContaining({
        paths: [FILM_PATH],
        confirm_final_removal: true,
      }),
    ]);
    fireEvent.click(within(outcome).getByRole("button", { name: "Close" }));
    expect(within(drawer).queryByTestId("library-outcome")).toBeNull();
  });

  it("says in the panel that the clean could not start, changes nothing, and lets the button be pressed again", async () => {
    api = stubLibraryServer([FILM], {
      [LIBRARY_ROUTES.clean]: unexpectedProblem(),
    });
    renderLibraryPage();
    const drawer = await openTheFilm();
    await within(drawer).findByText(/Audio · 2 tracks/);

    fireEvent.click(cleanButton(drawer));

    expect(await within(drawer).findByRole("alert")).toHaveTextContent(
      START_FAILED,
    );
    expect(screen.queryByTestId("library-confirm")).toBeNull();
    expect(within(drawer).queryByTestId("library-outcome")).toBeNull();
    expect(api.to(LIBRARY_ROUTES.clean)).toHaveLength(1);
    expect(cleanButton(drawer)).toBeEnabled();
  });

  it("offers no clean of a file whose rules it already matches", async () => {
    api = stubLibraryServer([matchingFile()]);
    renderLibraryPage();
    const drawer = await openTheFilm();

    expect(cleanButton(drawer)).toBeDisabled();
  });
});

/** A server that remembers whether the film is Left alone, as the real one does. */
function stubServerRemembering(
  leftAlone: boolean,
  leaveAloneAnswer: unknown = undefined,
) {
  const state = { leftAlone };
  const film = () =>
    libraryFile({
      leave_alone: state.leftAlone,
      status: state.leftAlone ? "left_alone" : "needs_cleaning",
    });
  api = stubLibraryServer([film()], {
    [LIBRARY_ROUTES.files]: () => libraryFilesPage([film()]),
    [LIBRARY_ROUTES.overview]: () => libraryOverview([film()]),
    [LIBRARY_ROUTES.leaveAlone]: (request: StubbedRequest) => {
      if (leaveAloneAnswer !== undefined) return leaveAloneAnswer;
      const asked = request.body as { path: string; leave_alone: boolean };
      state.leftAlone = asked.leave_alone;
      return { path: asked.path, leave_alone: asked.leave_alone };
    },
  });
  return state;
}

const leftAloneBox = (drawer: HTMLElement) =>
  within(drawer).getByRole("checkbox", { name: /Left alone/ });

describe("a file set aside in its panel", () => {
  it("is Left alone once the box is ticked: the request carries the file, the panel says it is saved, and the file cannot be cleaned", async () => {
    stubServerRemembering(false);
    renderLibraryPage();
    const drawer = await openTheFilm();
    await within(drawer).findByText(/Audio · 2 tracks/);
    expect(cleanButton(drawer)).toBeEnabled();

    fireEvent.click(leftAloneBox(drawer));

    await waitFor(() =>
      expect(
        within(drawer).getByTestId("library-leave-alone-status"),
      ).toHaveTextContent("Saved. Weir will leave this file alone."),
    );
    expect(api.to(LIBRARY_ROUTES.leaveAlone)[0].body).toMatchObject({
      path: FILM_PATH,
      leave_alone: true,
    });
    expect(leftAloneBox(drawer)).toBeChecked();
    expect(cleanButton(drawer)).toBeDisabled();
    expect(within(drawer).getAllByText("Left alone").length).toBeGreaterThan(0);
  });

  it("shows a file that is already Left alone as Left alone, and cannot be cleaned from the panel", async () => {
    stubServerRemembering(true);
    renderLibraryPage();

    const drawer = await openTheFilm();

    expect(leftAloneBox(drawer)).toBeChecked();
    expect(cleanButton(drawer)).toBeDisabled();
    expect(within(drawer).getAllByText("Left alone").length).toBeGreaterThan(0);
    expect(api.to(LIBRARY_ROUTES.clean)).toHaveLength(0);
  });

  it("can be cleaned again once the box is unticked, and the panel says so", async () => {
    stubServerRemembering(true);
    renderLibraryPage();
    const drawer = await openTheFilm();
    await within(drawer).findByText(/Audio · 2 tracks/);

    fireEvent.click(leftAloneBox(drawer));

    await waitFor(() =>
      expect(
        within(drawer).getByTestId("library-leave-alone-status"),
      ).toHaveTextContent("Saved. This file can be cleaned again."),
    );
    expect(api.to(LIBRARY_ROUTES.leaveAlone)[0].body).toMatchObject({
      path: FILM_PATH,
      leave_alone: false,
    });
    expect(leftAloneBox(drawer)).not.toBeChecked();
    await waitFor(() => expect(cleanButton(drawer)).toBeEnabled());
  });

  it("stays as it was, with the reason shown, when the choice cannot be saved", async () => {
    stubServerRemembering(false, unexpectedProblem());
    renderLibraryPage();
    const drawer = await openTheFilm();
    await within(drawer).findByText(/Audio · 2 tracks/);

    fireEvent.click(leftAloneBox(drawer));

    expect(await within(drawer).findByRole("alert")).toHaveTextContent(
      SAVE_FAILED,
    );
    expect(leftAloneBox(drawer)).not.toBeChecked();
    expect(leftAloneBox(drawer)).toBeEnabled();
    expect(cleanButton(drawer)).toBeEnabled();
    expect(
      within(drawer).getByTestId("library-leave-alone-status"),
    ).toBeEmptyDOMElement();
  });

  it("stays unticked and says the server's own reason when it refuses the choice", async () => {
    stubServerRemembering(
      false,
      jsonAnswer({ detail: "That file is no longer in this library." }, 404),
    );
    renderLibraryPage();
    const drawer = await openTheFilm();
    await within(drawer).findByText(/Audio · 2 tracks/);

    fireEvent.click(leftAloneBox(drawer));

    expect(await within(drawer).findByRole("alert")).toHaveTextContent(
      "That file is no longer in this library.",
    );
    expect(leftAloneBox(drawer)).not.toBeChecked();
  });
});
