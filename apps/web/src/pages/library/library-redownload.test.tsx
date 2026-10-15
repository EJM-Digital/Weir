import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type {
  LibraryRedownloadResult,
  LibraryRedownloadTitle,
} from "../../lib/processing/library-redownload-api";
import { LibraryRedownload } from "./library-redownload";

const mutate = vi.fn();
let title: LibraryRedownloadTitle;
let result: LibraryRedownloadResult | undefined;

vi.mock("../../lib/processing/library-mode-queries", () => ({
  useLibraryRedownloadsQuery: () => ({
    data: { library_id: 1, titles: [title], total: 1 },
    isError: false,
  }),
  useRequestLibraryRedownload: () => ({
    mutate,
    data: result,
    isPending: false,
    isError: false,
    error: null,
  }),
}));

const PATH = "/library/films/Arrival (2016)/Arrival.mkv";

function offered(overrides: Partial<LibraryRedownloadTitle> = {}) {
  return {
    path: PATH,
    manager_kind: "radarr",
    manager_title: "Arrival",
    removed_tracks: [
      {
        language: "eng",
        type: "audio",
        codec: "eac3",
        variant: null,
        reason: "Not a language your rules keep",
      },
    ],
    can_redownload: true,
    confirmation_message: "Arrival's current file will be deleted.",
    unavailable_reason: null,
    waiting_note: null,
    ...overrides,
  } satisfies LibraryRedownloadTitle;
}

function answer(outcome: string, message: string): LibraryRedownloadResult {
  return { path: PATH, outcome, message };
}

describe("LibraryRedownload", () => {
  beforeEach(() => {
    mutate.mockReset();
    title = offered();
    result = undefined;
  });

  it("asks for a confirmation before sending the request, and sends only the path", () => {
    render(<LibraryRedownload libraryId={1} path={PATH} />);

    fireEvent.click(screen.getByRole("button", { name: "Download again" }));
    expect(mutate).not.toHaveBeenCalled();
    expect(
      screen.getByText("Arrival's current file will be deleted."),
    ).toBeInTheDocument();

    fireEvent.click(
      screen.getByRole("button", { name: "Yes, download it again" }),
    );
    expect(mutate).toHaveBeenCalledWith(PATH, expect.anything());
  });

  it.each([
    [
      "requested",
      "Radarr on nas deleted the existing file and is searching for a replacement.",
    ],
    [
      "deleted_but_search_failed",
      "Radarr on nas deleted the existing file, but Weir could not ask it to search for a replacement.",
    ],
    [
      "delete_not_confirmed",
      "Radarr on nas did not confirm that it deleted the file, so Weir cannot tell whether the file is still there.",
    ],
    [
      "file_not_matched",
      "Radarr on nas does not list a file at that path, so Weir deleted nothing and asked for no search.",
    ],
    [
      "failed",
      "Weir could not reach Radarr on nas at http://nas:7878. Check the address is right.",
    ],
  ])("shows the server's own words for %s", (outcome, message) => {
    result = answer(outcome, message);

    render(<LibraryRedownload libraryId={1} path={PATH} />);

    expect(screen.getByRole("status")).toHaveTextContent(message);
  });

  it("stops offering the button once the manager has the request, and not before", () => {
    result = answer("requested", "Radarr on nas is searching.");
    const { unmount } = render(<LibraryRedownload libraryId={1} path={PATH} />);
    expect(
      screen.queryByRole("button", { name: "Download again" }),
    ).not.toBeInTheDocument();
    unmount();

    for (const outcome of [
      "deleted_but_search_failed",
      "delete_not_confirmed",
      "file_not_matched",
      "failed",
    ]) {
      result = answer(outcome, "Try again.");
      const view = render(<LibraryRedownload libraryId={1} path={PATH} />);
      expect(
        screen.getByRole("button", { name: "Download again" }),
      ).toBeInTheDocument();
      view.unmount();
    }
  });

  it("shows the note on a title whose file is gone and whose search did not go through", () => {
    title = offered({
      waiting_note:
        "Radarr on nas deleted the file, but no search for a new copy went through. Use Download again to search once more.",
    });

    render(<LibraryRedownload libraryId={1} path={PATH} />);

    expect(screen.getByTestId("library-redownload-waiting")).toHaveTextContent(
      "no search for a new copy went through",
    );
  });

  it("shows no waiting note for a title nothing is waiting on", () => {
    render(<LibraryRedownload libraryId={1} path={PATH} />);

    expect(
      screen.queryByTestId("library-redownload-waiting"),
    ).not.toBeInTheDocument();
  });
});
