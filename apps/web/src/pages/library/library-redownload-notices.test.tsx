import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";

import { LibraryRedownload } from "./library-redownload";

const PATH = "/library/films/Arrival (2016)/Arrival.mkv";
const REDOWNLOADS_URL = "/api/v1/processing/libraries/1/library-redownloads";

function answer(response: Response) {
  vi.stubGlobal(
    "fetch",
    vi.fn((input: RequestInfo | URL) =>
      String(input).endsWith(REDOWNLOADS_URL)
        ? Promise.resolve(response)
        : Promise.reject(new TypeError(`unexpected request to ${input}`)),
    ),
  );
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

afterEach(() => {
  vi.unstubAllGlobals();
});

it("says Weir could not check whether the file is missing tracks when the list cannot be read", async () => {
  answer(new Response("{}", { status: 500 }));

  open();

  expect(
    await screen.findByText(
      /Weir couldn’t check whether this file is missing tracks/,
    ),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Download again" }),
  ).not.toBeInTheDocument();
});

it("gives the reason instead of a Download again button when the title cannot be asked for again", async () => {
  answer(
    new Response(
      JSON.stringify({
        library_id: 1,
        total: 1,
        titles: [
          {
            path: PATH,
            manager_kind: null,
            manager_title: null,
            removed_tracks: [
              {
                language: "fre",
                type: "audio",
                codec: "ac3",
                variant: null,
                reason: "Not a language your rules keep",
              },
            ],
            can_redownload: false,
            confirmation_message: null,
            waiting_note: null,
            unavailable_reason:
              "Weir does not know which manager file this is, so it cannot ask for a redownload automatically. Scan the library again.",
          },
        ],
      }),
      { status: 200, headers: { "Content-Type": "application/json" } },
    ),
  );

  open();

  expect(
    await screen.findByText(/Scan the library again\./),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Download again" }),
  ).not.toBeInTheDocument();
});
