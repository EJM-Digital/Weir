import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";

import { signedInAs } from "../../../../test/signed-in-as";
import * as api from "../../../../lib/download-clients/download-clients-api";
import * as settingsQueries from "../../../../lib/settings/queries";
import { DownloadClientsTab } from "./download-clients-tab";

function wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
}

beforeEach(() => {
  signedInAs("admin");
  vi.spyOn(settingsQueries, "useAppSettingsQuery").mockReturnValue({
    data: undefined,
  } as ReturnType<typeof settingsQueries.useAppSettingsQuery>);
  vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([]);
});

afterEach(() => {
  vi.restoreAllMocks();
});

it("says what a download client is for while none is connected", async () => {
  render(<DownloadClientsTab />, { wrapper });

  expect(await screen.findByTestId("download-clients-empty")).toHaveTextContent(
    "Weir can still suggest watched folders from Sonarr, Radarr or Deluno",
  );
});

it("offers to add a download client", async () => {
  render(<DownloadClientsTab />, { wrapper });

  expect(
    await screen.findByRole("button", { name: "Add download client" }),
  ).toBeInTheDocument();
});
