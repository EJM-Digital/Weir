import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, expect, it, vi } from "vitest";

import * as api from "../../../../lib/media-managers/media-managers-api";
import type { MediaManagerConnection } from "../../../../lib/media-managers/media-managers-api";
import * as librariesApi from "../../../../lib/processing/libraries-api";
import type { ProcessingLibrary } from "../../../../lib/processing/libraries-api";
import { MediaManagersTab } from "./media-managers-tab";
import { stubMediaManagersTabNeighbours } from "./stub-media-managers-tab-neighbours";

function connection(
  over: Partial<MediaManagerConnection>,
): MediaManagerConnection {
  return {
    id: 1,
    kind: "deluno",
    name: "Deluno",
    enabled: true,
    base_url: "http://192.0.2.10:5099",
    api_key_is_saved: true,
    webhook_secret_is_set: true,
    webhook_url_path: "/api/v1/intake/webhook/deluno",
    downloaded_scan_enabled: false,
    unsigned_webhook_warning: null,
    last_test_ok: true,
    last_test_at: "2026-09-29T10:00:00Z",
    last_test_detail: null,
    ...over,
  };
}

function workflow(
  id: number,
  name: string,
  links: number[],
): ProcessingLibrary {
  return {
    id,
    name,
    media_type: "movie",
    manager_connection_ids: links,
  } as ProcessingLibrary;
}

function wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <MemoryRouter>
      <QueryClientProvider client={qc}>{children}</QueryClientProvider>
    </MemoryRouter>
  );
}

beforeEach(() => {
  stubMediaManagersTabNeighbours();
  vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
    connection({}),
    connection({ id: 2, kind: "native", name: "Home script" }),
  ]);
});

afterEach(() => {
  vi.restoreAllMocks();
});

it("lists the workflows each connection feeds and offers to start one from what it reports", async () => {
  vi.spyOn(librariesApi, "fetchProcessingLibraries").mockResolvedValue([
    workflow(1, "Movies from Deluno", [1]),
    workflow(2, "TV from Deluno", [1]),
  ]);

  render(<MediaManagersTab />, { wrapper });

  const lists = await screen.findAllByTestId("media-manager-libraries");
  expect(lists[0]).toHaveTextContent(
    "Workflows it feeds: Movies from Deluno, TV from Deluno",
  );
  expect(
    screen.getByRole("link", { name: "Add a workflow from Deluno" }),
  ).toHaveAttribute("href", "/setup/workflows?addFrom=1");
  expect(lists[1]).toHaveTextContent("No workflow is linked to it yet.");
  expect(
    screen.queryByRole("link", { name: "Add a workflow from Home script" }),
  ).not.toBeInTheDocument();
});

it("names the Weir only workflows, so a connection's list is not read as all of them", async () => {
  vi.spyOn(librariesApi, "fetchProcessingLibraries").mockResolvedValue([
    workflow(1, "Movies from Deluno", [1]),
    workflow(2, "Kids", []),
    workflow(3, "Home videos", []),
  ]);

  render(<MediaManagersTab />, { wrapper });

  expect(await screen.findByTestId("weir-only-workflows")).toHaveTextContent(
    "Weir only, with no media manager involved: Kids, Home videos.",
  );
});

it("says its for a single Weir only workflow", async () => {
  vi.spyOn(librariesApi, "fetchProcessingLibraries").mockResolvedValue([
    workflow(1, "Movies from Deluno", [1]),
    workflow(2, "Kids", []),
  ]);

  render(<MediaManagersTab />, { wrapper });

  expect(await screen.findByTestId("weir-only-workflows")).toHaveTextContent(
    "Weir only, with no media manager involved: Kids. Weir watches its folder and writes cleaned files to its output folder by itself.",
  );
});

it("says nothing about Weir only workflows when every workflow is linked", async () => {
  vi.spyOn(librariesApi, "fetchProcessingLibraries").mockResolvedValue([
    workflow(1, "Movies from Deluno", [1]),
  ]);

  render(<MediaManagersTab />, { wrapper });

  await screen.findAllByTestId("media-manager-libraries");
  expect(screen.queryByTestId("weir-only-workflows")).not.toBeInTheDocument();
});
