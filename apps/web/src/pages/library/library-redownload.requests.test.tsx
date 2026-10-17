import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type {
  LibraryRedownloadResult,
  LibraryRedownloadTitle,
} from "../../lib/processing/library-redownload-api";
import {
  jsonAnswer,
  stubWeirApi,
  unexpectedProblem,
  type StubbedApi,
} from "../../test/stub-weir-api";
import { LibraryRedownload } from "./library-redownload";

const LIST = "GET /api/v1/processing/libraries/1/library-redownloads";
const REQUEST = "POST /api/v1/processing/libraries/1/library-redownloads";
const PATH = "/library/films/Arrival (2016)/Arrival.mkv";
const CSRF_TOKEN = "csrf-for-this-test";
const FAILED_TO_ASK =
  "Weir couldn't ask your media manager. Nothing was deleted; try again.";

let api: StubbedApi;

function title(
  partial: Partial<LibraryRedownloadTitle> = {},
): LibraryRedownloadTitle {
  return {
    path: PATH,
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
    confirmation_message: "Radarr will delete Arrival's current file first.",
    unavailable_reason: null,
    waiting_note: null,
    ...partial,
  };
}

function answer(
  outcome: LibraryRedownloadResult["outcome"],
  message: string,
): LibraryRedownloadResult {
  return { path: PATH, outcome, message };
}

function stubServer(
  titles: LibraryRedownloadTitle[],
  routes: Record<string, unknown> = {},
) {
  api = stubWeirApi({
    [LIST]: { library_id: 1, titles, total: titles.length },
    "GET /api/v1/auth/csrf": { csrf_token: CSRF_TOKEN },
    [REQUEST]: answer(
      "requested",
      "Radarr on nas deleted the existing file and is searching for a replacement.",
    ),
    ...routes,
  });
}

function open() {
  render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <LibraryRedownload libraryId={1} path={PATH} />
    </QueryClientProvider>,
  );
}

const downloadAgain = () =>
  screen.findByRole("button", { name: "Download again" });

async function confirm() {
  fireEvent.click(await downloadAgain());
  fireEvent.click(
    await screen.findByRole("button", { name: "Yes, download it again" }),
  );
}

afterEach(() => {
  expect(api.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

describe("Download again is offered only for a file a past clean took tracks from", () => {
  it("is offered, with what was lost, for a title the rules would now keep tracks of", async () => {
    stubServer([title()]);

    open();

    expect(await downloadAgain()).toBeEnabled();
    expect(
      screen.getByText(/A past clean removed French audio from this file/),
    ).toBeInTheDocument();
    expect(screen.getByText(/get it back is to download/)).toBeInTheDocument();
  });

  it("says them, not it, when more than one track was lost", async () => {
    stubServer([
      title({
        removed_tracks: [
          {
            language: "fre",
            type: "audio",
            codec: "ac3",
            variant: null,
            reason: "Not a language your rules kept at the time",
          },
          {
            language: "eng",
            type: "subtitle",
            codec: "subrip",
            variant: null,
            reason: "Not a language your rules kept at the time",
          },
        ],
      }),
    ]);

    open();

    expect(
      await screen.findByText(
        /A past clean removed French audio, English subtitles from this file/,
      ),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/get them back is to download/),
    ).toBeInTheDocument();
  });

  it("gives the reason instead of the button when the manager cannot be asked", async () => {
    stubServer([
      title({
        can_redownload: false,
        unavailable_reason:
          "Weir does not know which manager file this is, so it cannot ask for a redownload automatically. Scan the library again.",
      }),
    ]);

    open();

    expect(await screen.findByText(/Scan the library again\./)).toBeVisible();
    expect(screen.queryByRole("button", { name: "Download again" })).toBeNull();
  });

  it("offers nothing, and draws nothing, for a file no past clean took tracks from", async () => {
    stubServer([title({ path: "/library/films/Heat (1995)/Heat.mkv" })]);

    open();

    await waitFor(() => expect(api.to(LIST)).toHaveLength(1));
    await waitFor(() =>
      expect(screen.queryByTestId("library-redownload")).toBeNull(),
    );
    expect(screen.queryByRole("button", { name: "Download again" })).toBeNull();
  });
});

describe("asking the media manager to download a title again", () => {
  it("asks first, and sends nothing if it is not confirmed", async () => {
    stubServer([title()]);
    open();

    fireEvent.click(await downloadAgain());

    expect(
      await screen.findByText(
        "Radarr will delete Arrival's current file first.",
      ),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Not now" }));
    expect(screen.queryByTestId("library-redownload-confirm")).toBeNull();
    expect(api.to(REQUEST)).toHaveLength(0);
  });

  it("sends the path with the confirmation, says what the manager did, and stops offering the button", async () => {
    stubServer([title()]);
    open();

    await confirm();

    expect(await screen.findByRole("status")).toHaveTextContent(
      "Radarr on nas deleted the existing file and is searching for a replacement.",
    );
    expect(api.to(REQUEST)).toHaveLength(1);
    expect(api.to(REQUEST)[0].body).toEqual({
      path: PATH,
      confirm_destructive: true,
      csrf_token: CSRF_TOKEN,
    });
    expect(screen.queryByTestId("library-redownload-confirm")).toBeNull();
    expect(screen.queryByRole("button", { name: "Download again" })).toBeNull();
  });

  it("says Asking your media manager… and holds the button while the manager answers", async () => {
    let reply: () => void = () => undefined;
    const replied = new Promise<void>((resolve) => (reply = resolve));
    stubServer([title()], {
      [REQUEST]: async () => {
        await replied;
        return answer("requested", "Radarr on nas is searching.");
      },
    });
    open();

    await confirm();

    const busy = await screen.findAllByRole("button", {
      name: "Asking your media manager…",
    });
    expect(busy.length).toBeGreaterThan(0);
    busy.forEach((button) => expect(button).toBeDisabled());
    reply();
    expect(await screen.findByRole("status")).toHaveTextContent(
      "Radarr on nas is searching.",
    );
  });

  it.each([
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
  ] as const)(
    "says the server's own words for %s, and lets the person ask again",
    async (outcome, message) => {
      stubServer([title()], { [REQUEST]: answer(outcome, message) });
      open();

      await confirm();

      expect(await screen.findByRole("status")).toHaveTextContent(message);
      expect(await downloadAgain()).toBeEnabled();
    },
  );

  it("says nothing was deleted when the request itself fails, and lets the person try again", async () => {
    stubServer([title()], { [REQUEST]: unexpectedProblem() });
    open();

    await confirm();

    expect(await screen.findByRole("alert")).toHaveTextContent(FAILED_TO_ASK);
    expect(screen.queryByTestId("library-redownload-confirm")).toBeNull();
    expect(await downloadAgain()).toBeEnabled();
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("gives the server's own reason when it refuses the request", async () => {
    stubServer([title()], {
      [REQUEST]: jsonAnswer(
        { detail: "A viewer cannot ask for a title to be downloaded again." },
        403,
      ),
    });
    open();

    await confirm();

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "A viewer cannot ask for a title to be downloaded again.",
    );
  });

  it("says Weir cannot be reached when the server does not answer", async () => {
    stubServer([title()], {
      [REQUEST]: () => {
        throw new TypeError("Failed to fetch");
      },
    });
    open();

    await confirm();

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Can't reach Weir",
    );
  });
});

describe("a title whose file is gone and whose search did not go through", () => {
  it("says so, and still offers Download again to search once more", async () => {
    stubServer([
      title({
        waiting_note:
          "Radarr on nas deleted the file, but no search for a new copy went through. Use Download again to search once more.",
      }),
    ]);

    open();

    expect(
      await screen.findByTestId("library-redownload-waiting"),
    ).toHaveTextContent("no search for a new copy went through");
    expect(await downloadAgain()).toBeEnabled();
  });
});
