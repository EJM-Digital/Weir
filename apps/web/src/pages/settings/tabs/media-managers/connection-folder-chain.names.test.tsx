import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, expect, it, vi } from "vitest";

import * as librariesApi from "../../../../lib/processing/libraries-api";
import * as chainApi from "../../../../lib/processing/library-folder-chain-api";
import { ConnectionFolderChain } from "./connection-folder-chain";

function wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return (
    <MemoryRouter>
      <QueryClientProvider client={qc}>{children}</QueryClientProvider>
    </MemoryRouter>
  );
}

afterEach(() => {
  vi.restoreAllMocks();
});

it("names a workflow by its number when the list of workflows does not have it", async () => {
  vi.spyOn(chainApi, "fetchConnectionFolderChain").mockResolvedValue([
    {
      library_id: 12,
      local: { ready: true, lines: [] },
      managers: [],
      download_clients: [],
      ready: true,
    },
  ]);
  vi.spyOn(librariesApi, "fetchProcessingLibraries").mockResolvedValue([]);

  render(<ConnectionFolderChain connectionId={3} />, { wrapper });

  expect(await screen.findByText("Workflow 12")).toBeInTheDocument();
});
