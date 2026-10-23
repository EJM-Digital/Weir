import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, expect, it, vi } from "vitest";

import * as downloadClientsApi from "../../../../lib/download-clients/download-clients-api";
import type { DownloadClientSuggestion } from "../../../../lib/download-clients/download-clients-api";
import { LibraryDownloadClientSuggestions } from "./library-download-client-suggestions";

function wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
}

const sabnzbd: DownloadClientSuggestion = {
  connection_id: 9,
  kind: "sabnzbd",
  name: "SABnzbd",
  label: "SABnzbd",
  suggested_watched_folder: "/downloads/complete",
  category_folders: [],
};

afterEach(() => {
  vi.restoreAllMocks();
});

it("offers a download client's own folder as a watched folder, to a workflow linked to nothing", async () => {
  const onUseFolder = vi.fn();
  vi.spyOn(
    downloadClientsApi,
    "fetchDownloadClientSuggestions",
  ).mockResolvedValue([sabnzbd]);

  render(
    <LibraryDownloadClientSuggestions
      mediaType="movie"
      watchedFolder="/media/movies"
      editable
      onUseFolder={onUseFolder}
    />,
    { wrapper },
  );

  expect(await screen.findByText("/downloads/complete")).toBeInTheDocument();
  fireEvent.click(
    screen.getByRole("button", { name: "Use this as the watched folder" }),
  );
  expect(onUseFolder).toHaveBeenCalledWith("/downloads/complete");
});

it("offers a folder that is not the watched folder without calling it Ready or Fine", async () => {
  vi.spyOn(
    downloadClientsApi,
    "fetchDownloadClientSuggestions",
  ).mockResolvedValue([sabnzbd]);

  render(
    <LibraryDownloadClientSuggestions
      mediaType="movie"
      watchedFolder="/media/movies"
      editable
      onUseFolder={() => {}}
    />,
    { wrapper },
  );

  const offer = await screen.findByTestId(
    "library-download-client-suggestions",
  );
  expect(offer).toHaveTextContent(
    "SABnzbd's completed-downloads folder is /downloads/complete.",
  );
  expect(offer).not.toHaveTextContent(/ready|fine|✓/i);
});

it("offers nothing once the watched folder already is the client's folder", async () => {
  const fetch = vi
    .spyOn(downloadClientsApi, "fetchDownloadClientSuggestions")
    .mockResolvedValue([sabnzbd]);

  render(
    <LibraryDownloadClientSuggestions
      mediaType="movie"
      watchedFolder="/downloads/complete"
      editable
      onUseFolder={() => {}}
    />,
    { wrapper },
  );

  await waitFor(() => expect(fetch).toHaveBeenCalled());
  expect(
    screen.queryByTestId("library-download-client-suggestions"),
  ).not.toBeInTheDocument();
});

it("shows a person who may not edit the folder without the button to use it", async () => {
  vi.spyOn(
    downloadClientsApi,
    "fetchDownloadClientSuggestions",
  ).mockResolvedValue([sabnzbd]);

  render(
    <LibraryDownloadClientSuggestions
      mediaType="movie"
      watchedFolder="/media/movies"
      editable={false}
      onUseFolder={() => {}}
    />,
    { wrapper },
  );

  expect(await screen.findByText("/downloads/complete")).toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Use this as the watched folder" }),
  ).not.toBeInTheDocument();
});

it("offers nothing while the download clients are still being read", async () => {
  const fetch = vi
    .spyOn(downloadClientsApi, "fetchDownloadClientSuggestions")
    .mockReturnValue(new Promise(() => undefined));

  render(
    <LibraryDownloadClientSuggestions
      mediaType="movie"
      watchedFolder="/media/movies"
      editable
      onUseFolder={() => {}}
    />,
    { wrapper },
  );

  await waitFor(() => expect(fetch).toHaveBeenCalled());
  expect(
    screen.queryByTestId("library-download-client-suggestions"),
  ).not.toBeInTheDocument();
});
