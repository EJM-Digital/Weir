import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
  cleanQueued,
  confirmationRequired,
  libraryFile,
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
const START_FAILED =
  "Weir couldn't start the clean. Nothing was changed. Try again.";
const CHECK_FAILED = "Weir couldn't start a check. Try again in a moment.";

let api: StubbedApi;

const isConfirmed = (request: StubbedRequest) =>
  (request.body as { confirm_final_removal: boolean }).confirm_final_removal;

beforeEach(() => {
  localStorage.clear();
});

afterEach(() => {
  expect(api.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

async function selectTheFilm() {
  const row = await screen.findByTestId("library-row");
  fireEvent.click(within(row).getByRole("checkbox"));
  return screen.getByTestId("library-bulk");
}

describe("a clean of the selection that cannot start", () => {
  it("says so under the selection bar when asking what the clean would remove fails, queues nothing, and keeps the selection", async () => {
    api = stubLibraryServer([FILM], {
      [LIBRARY_ROUTES.clean]: unexpectedProblem(),
    });
    renderLibraryPage();
    const bulk = await selectTheFilm();

    fireEvent.click(
      within(bulk).getByRole("button", { name: "Clean these files" }),
    );

    expect(await screen.findByRole("alert")).toHaveTextContent(START_FAILED);
    expect(screen.queryByTestId("library-confirm")).toBeNull();
    expect(screen.queryByTestId("library-outcome")).toBeNull();
    expect(api.to(LIBRARY_ROUTES.clean)).toHaveLength(1);
    expect(isConfirmed(api.to(LIBRARY_ROUTES.clean)[0])).toBe(false);
    expect(bulk).toHaveTextContent("1 selected");
    expect(
      within(bulk).getByRole("button", { name: "Clean these files" }),
    ).toBeEnabled();
  });

  it("gives the server's own reason when it refuses, in place of the general sentence", async () => {
    api = stubLibraryServer([FILM], {
      [LIBRARY_ROUTES.clean]: jsonAnswer(
        { detail: "A viewer cannot clean files." },
        403,
      ),
    });
    renderLibraryPage();
    const bulk = await selectTheFilm();

    fireEvent.click(
      within(bulk).getByRole("button", { name: "Clean these files" }),
    );

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "A viewer cannot clean files.",
    );
  });

  it("says Weir cannot be reached when the server does not answer", async () => {
    api = stubLibraryServer([FILM], {
      [LIBRARY_ROUTES.clean]: () => {
        throw new TypeError("Failed to fetch");
      },
    });
    renderLibraryPage();
    const bulk = await selectTheFilm();

    fireEvent.click(
      within(bulk).getByRole("button", { name: "Clean these files" }),
    );

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Can't reach Weir",
    );
  });

  it("clears the message when the clean is started again and goes through", async () => {
    api = stubLibraryServer([FILM], {
      [LIBRARY_ROUTES.clean]: unexpectedProblem(),
    });
    renderLibraryPage();
    const bulk = await selectTheFilm();
    fireEvent.click(
      within(bulk).getByRole("button", { name: "Clean these files" }),
    );
    await screen.findByRole("alert");
    api.set(
      LIBRARY_ROUTES.clean,
      jsonAnswer(confirmationRequired(1, 1, 333_000_000), 400),
    );

    fireEvent.click(
      within(bulk).getByRole("button", { name: "Clean these files" }),
    );

    expect(await screen.findByTestId("library-confirm")).toBeInTheDocument();
    expect(screen.queryByText(START_FAILED)).toBeNull();
  });
});

describe("a confirmed clean that cannot start", () => {
  function askAndConfirm() {
    api = stubLibraryServer([FILM], {
      [LIBRARY_ROUTES.clean]: (request: StubbedRequest) =>
        isConfirmed(request)
          ? unexpectedProblem()
          : jsonAnswer(confirmationRequired(1, 1, 333_000_000), 400),
    });
    renderLibraryPage();
  }

  it("says so inside the confirmation, which stays open, and queues nothing", async () => {
    askAndConfirm();
    const bulk = await selectTheFilm();
    fireEvent.click(
      within(bulk).getByRole("button", { name: "Clean these files" }),
    );
    const dialog = await screen.findByTestId("library-confirm");

    fireEvent.click(within(dialog).getByTestId("library-confirm-confirm"));

    expect(await within(dialog).findByRole("alert")).toHaveTextContent(
      START_FAILED,
    );
    expect(screen.getByTestId("library-confirm")).toBeInTheDocument();
    expect(within(dialog).getByTestId("library-confirm-confirm")).toBeEnabled();
    expect(screen.queryByTestId("library-outcome")).toBeNull();
    expect(api.to(LIBRARY_ROUTES.clean).map(isConfirmed)).toEqual([
      false,
      true,
    ]);
    expect(bulk).toHaveTextContent("1 selected");
  });

  it("closes the confirmation and says what was queued when confirming is tried again and goes through", async () => {
    askAndConfirm();
    const bulk = await selectTheFilm();
    fireEvent.click(
      within(bulk).getByRole("button", { name: "Clean these files" }),
    );
    const dialog = await screen.findByTestId("library-confirm");
    fireEvent.click(within(dialog).getByTestId("library-confirm-confirm"));
    await within(dialog).findByRole("alert");
    api.set(LIBRARY_ROUTES.clean, cleanQueued(1));

    fireEvent.click(within(dialog).getByTestId("library-confirm-confirm"));

    await waitFor(() =>
      expect(screen.queryByTestId("library-confirm")).toBeNull(),
    );
    expect(await screen.findByTestId("library-outcome")).toHaveTextContent(
      "1 file is queued to clean.",
    );
    expect(screen.queryByTestId("library-bulk")).toBeNull();
  });

  it("leaves the selection in place when the confirmation is cancelled after a failure", async () => {
    askAndConfirm();
    const bulk = await selectTheFilm();
    fireEvent.click(
      within(bulk).getByRole("button", { name: "Clean these files" }),
    );
    const dialog = await screen.findByTestId("library-confirm");
    fireEvent.click(within(dialog).getByTestId("library-confirm-confirm"));
    await within(dialog).findByRole("alert");

    fireEvent.click(within(dialog).getByTestId("library-confirm-cancel"));

    expect(screen.queryByTestId("library-confirm")).toBeNull();
    expect(screen.queryByText(START_FAILED)).toBeNull();
    expect(screen.getByTestId("library-bulk")).toHaveTextContent("1 selected");
  });
});

describe("Check again that cannot start", () => {
  it("says Weir couldn't start a check beside the scan status, and the button can be pressed again", async () => {
    api = stubLibraryServer([FILM], {
      [LIBRARY_ROUTES.scan]: unexpectedProblem(),
    });
    renderLibraryPage();
    await screen.findByTestId("library-row");

    fireEvent.click(screen.getByRole("button", { name: "Check again" }));

    expect(await screen.findByTestId("library-scan-error")).toHaveTextContent(
      CHECK_FAILED,
    );
    expect(screen.getByTestId("library-scan-error")).toHaveAttribute(
      "role",
      "alert",
    );
    expect(api.to(LIBRARY_ROUTES.scan)).toHaveLength(1);
    expect(screen.getByRole("button", { name: "Check again" })).toBeEnabled();
  });

  it("gives the server's own reason when it refuses the check", async () => {
    api = stubLibraryServer([FILM], {
      [LIBRARY_ROUTES.scan]: jsonAnswer(
        { detail: "This workflow has no folders to check yet." },
        400,
      ),
    });
    renderLibraryPage();
    await screen.findByTestId("library-row");

    fireEvent.click(screen.getByRole("button", { name: "Check again" }));

    expect(await screen.findByTestId("library-scan-error")).toHaveTextContent(
      "This workflow has no folders to check yet.",
    );
  });

  it("shows no message when the check starts", async () => {
    api = stubLibraryServer([FILM], {
      [LIBRARY_ROUTES.scan]: {
        already_running: false,
        job_id: 9,
        status: "queued",
      },
    });
    renderLibraryPage();
    await screen.findByTestId("library-row");

    fireEvent.click(screen.getByRole("button", { name: "Check again" }));

    await waitFor(() => expect(api.to(LIBRARY_ROUTES.scan)).toHaveLength(1));
    expect(screen.queryByTestId("library-scan-error")).toBeNull();
  });
});
