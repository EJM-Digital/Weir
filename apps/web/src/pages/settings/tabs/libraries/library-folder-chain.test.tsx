import {
  QueryClient,
  QueryClientProvider,
  onlineManager,
} from "@tanstack/react-query";
import { render, screen, within } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";

import * as chainApi from "../../../../lib/processing/library-folder-chain-api";
import type { LibraryFolderChain as LibraryFolderChainData } from "../../../../lib/processing/library-folder-chain-api";
import { LibraryFolderChain } from "./library-folder-chain";
import { settleFolderChecks } from "./library-test-fixtures";
import { useFakeClock, useRealClock } from "../../../../test/fake-clock";

function wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
}

function chain(
  over: Partial<LibraryFolderChainData> = {},
): LibraryFolderChainData {
  return {
    library_id: 12,
    local: {
      ready: true,
      lines: [
        { state: "ok", text: "Weir can read the watched folder /media/in." },
      ],
    },
    managers: [],
    download_clients: [],
    ready: true,
    ...over,
  };
}

// The folders settle after a debounce; the tests move the clock instead of waiting for it.
beforeEach(() => {
  useFakeClock();
});

afterEach(() => {
  onlineManager.setOnline(true);
  useRealClock();
  vi.restoreAllMocks();
});

it("asks the library to save first when it has no id yet", () => {
  render(
    <LibraryFolderChain
      libraryId={undefined}
      watchedFolder="/media/in"
      workFolder=""
      outputFolder="/media/out"
      mediaType="movie"
    />,
    { wrapper },
  );

  expect(
    screen.getByText("Save this workflow first to check its folder chain."),
  ).toBeInTheDocument();
});

it("shows Weir's own folder lines are ready, with no manager section and no absence warning, when none is connected", async () => {
  vi.spyOn(chainApi, "fetchLibraryFolderChain").mockResolvedValue(chain());

  render(
    <LibraryFolderChain
      libraryId={12}
      watchedFolder="/media/in"
      workFolder=""
      outputFolder="/media/out"
      mediaType="movie"
    />,
    { wrapper },
  );
  await settleFolderChecks();

  const block = await screen.findByRole("region", {
    name: "Weir's own folders",
  });
  expect(
    within(block).getByText("Weir can read the watched folder /media/in."),
  ).toBeInTheDocument();
  expect(within(block).getByText("Ready")).toBeInTheDocument();
  expect(screen.queryByText(/no.*connection covers/i)).not.toBeInTheDocument();
});

it("surfaces a connected manager's own lines and readiness", async () => {
  vi.spyOn(chainApi, "fetchLibraryFolderChain").mockResolvedValue(
    chain({
      ready: false,
      managers: [
        {
          connection_id: 3,
          kind: "sonarr",
          name: "Sonarr",
          label: "Sonarr",
          flow: "remote_path_mapping",
          ready: false,
          mapping: null,
          lines: [
            {
              state: "problem",
              text: "Sonarr has no enabled download client, so it has no downloads to import.",
            },
          ],
        },
      ],
    }),
  );

  render(
    <LibraryFolderChain
      libraryId={12}
      watchedFolder="/media/in"
      workFolder=""
      outputFolder="/media/out"
      mediaType="tv"
    />,
    { wrapper },
  );
  await settleFolderChecks();

  const block = await screen.findByRole("region", { name: "Sonarr" });
  expect(within(block).getByText("Needs attention")).toBeInTheDocument();
  expect(
    within(block).getByText(
      "Sonarr has no enabled download client, so it has no downloads to import.",
    ),
  ).toBeInTheDocument();
});

it("surfaces a bare download client's own lines and readiness", async () => {
  vi.spyOn(chainApi, "fetchLibraryFolderChain").mockResolvedValue(
    chain({
      ready: false,
      download_clients: [
        {
          connection_id: 9,
          kind: "sabnzbd",
          name: "SABnzbd",
          label: "SABnzbd",
          ready: false,
          lines: [
            {
              state: "problem",
              text: "None of SABnzbd's folders match this workflow's watched folder /media/in.",
            },
          ],
        },
      ],
    }),
  );

  render(
    <LibraryFolderChain
      libraryId={12}
      watchedFolder="/media/in"
      workFolder=""
      outputFolder="/media/out"
      mediaType="tv"
    />,
    { wrapper },
  );
  await settleFolderChecks();

  const block = await screen.findByRole("region", { name: "SABnzbd" });
  expect(within(block).getByText("Needs attention")).toBeInTheDocument();
  expect(
    within(block).getByText(
      "None of SABnzbd's folders match this workflow's watched folder /media/in.",
    ),
  ).toBeInTheDocument();
});

it("does not show a green Ready for a manager whose folders Weir could only read as a declaration", async () => {
  vi.spyOn(chainApi, "fetchLibraryFolderChain").mockResolvedValue(
    chain({
      managers: [
        {
          connection_id: 4,
          kind: "deluno",
          name: "Deluno",
          label: "Deluno",
          flow: "handoff",
          ready: true,
          mapping: null,
          lines: [
            {
              state: "unverified",
              text: "Deluno says its Movies library downloads to /media/in (inside Weir's watched folder). Weir can't see where each download client really saves.",
            },
          ],
        },
      ],
    }),
  );

  render(
    <LibraryFolderChain
      libraryId={12}
      watchedFolder="/media/in"
      workFolder=""
      outputFolder="/media/out"
      mediaType="movie"
    />,
    { wrapper },
  );
  await settleFolderChecks();

  const block = await screen.findByRole("region", { name: "Deluno" });
  expect(within(block).getByText("Not verified")).toBeInTheDocument();
  expect(within(block).queryByText("Ready")).not.toBeInTheDocument();
});

it("says when it could not check just now", async () => {
  vi.spyOn(chainApi, "fetchLibraryFolderChain").mockRejectedValue(
    new Error("Could not reach the server."),
  );

  render(
    <LibraryFolderChain
      libraryId={12}
      watchedFolder="/media/in"
      workFolder=""
      outputFolder="/media/out"
      mediaType="movie"
    />,
    { wrapper },
  );
  await settleFolderChecks();

  expect(
    await screen.findByText(
      /Weir could not check this workflow's folder chain just now/,
    ),
  ).toBeInTheDocument();
});

it("shows no result while the browser is offline and the check cannot be sent", async () => {
  const fetchChain = vi
    .spyOn(chainApi, "fetchLibraryFolderChain")
    .mockResolvedValue(chain());
  onlineManager.setOnline(false);
  const client = new QueryClient();

  render(
    <QueryClientProvider client={client}>
      <LibraryFolderChain
        libraryId={12}
        watchedFolder="/media/in"
        workFolder=""
        outputFolder="/media/out"
        mediaType="movie"
      />
    </QueryClientProvider>,
  );
  await settleFolderChecks();

  expect(screen.getByTestId("library-folder-chain")).toBeEmptyDOMElement();
  expect(fetchChain).not.toHaveBeenCalled();
  client.clear();
});
