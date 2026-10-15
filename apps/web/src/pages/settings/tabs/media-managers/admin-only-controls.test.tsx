import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import * as downloadClientsApi from "../../../../lib/download-clients/download-clients-api";
import type { DownloadClientConnection } from "../../../../lib/download-clients/download-clients-api";
import * as api from "../../../../lib/media-managers/media-managers-api";
import type { MediaManagerConnection } from "../../../../lib/media-managers/media-managers-api";
import { signedInAs } from "../../../../test/signed-in-as";
import { DownloadClientsSection } from "./download-clients-section";
import { MediaManagersTab } from "./media-managers-tab";
import { stubMediaManagersTabNeighbours } from "./stub-media-managers-tab-neighbours";

const MANAGER: MediaManagerConnection = {
  id: 1,
  kind: "deluno",
  name: "Deluno",
  enabled: true,
  base_url: "http://192.0.2.10:5099",
  api_key_is_saved: true,
  webhook_secret_is_set: false,
  webhook_url_path: "/api/v1/intake/webhook/deluno",
  downloaded_scan_enabled: false,
  unsigned_webhook_warning: null,
  last_test_ok: null,
  last_test_at: null,
  last_test_detail: null,
};

const CLIENT: DownloadClientConnection = {
  id: 1,
  kind: "qbittorrent",
  name: "qBittorrent on 192.0.2.10",
  enabled: true,
  base_url: "http://192.0.2.10:8080",
  username: "admin",
  password_is_saved: true,
  api_key_is_saved: false,
  last_test_ok: null,
  last_test_at: null,
  last_test_detail: null,
};

function wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return (
    <MemoryRouter>
      <QueryClientProvider client={qc}>{children}</QueryClientProvider>
    </MemoryRouter>
  );
}

beforeEach(stubMediaManagersTabNeighbours);

afterEach(() => {
  vi.restoreAllMocks();
});

describe.each(["operator", "viewer"] as const)("signed in as a %s", (role) => {
  it("cannot add, change or remove a media manager, and is told why", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([MANAGER]);
    signedInAs(role);

    render(<MediaManagersTab />, { wrapper });

    expect(await screen.findByTestId("media-manager-remove")).toBeDisabled();
    expect(screen.getByTestId("media-manager-edit")).toBeDisabled();
    expect(screen.getByRole("button", { name: "Disable" })).toBeDisabled();
    expect(screen.getByTestId("media-manager-add")).toBeDisabled();
    expect(
      screen.getByText(
        "Only an admin can add, change or remove a media manager.",
      ),
    ).toBeInTheDocument();
    expect(screen.getByTestId("media-manager-test")).toBeEnabled();
  });

  it("cannot add a first media manager", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([]);
    signedInAs(role);

    render(<MediaManagersTab />, { wrapper });

    expect(
      await screen.findByRole("button", {
        name: "Add your first media manager",
      }),
    ).toBeDisabled();
  });

  it("cannot add, change or remove a download client, and is told why", async () => {
    vi.spyOn(
      downloadClientsApi,
      "fetchDownloadClientConnections",
    ).mockResolvedValue([CLIENT]);
    signedInAs(role);

    render(<DownloadClientsSection />, { wrapper });

    expect(await screen.findByTestId("download-client-remove")).toBeDisabled();
    expect(screen.getByTestId("download-client-edit")).toBeDisabled();
    expect(screen.getByRole("button", { name: "Disable" })).toBeDisabled();
    expect(screen.getByTestId("download-client-add")).toBeDisabled();
    expect(
      screen.getByText(
        "Only an admin can add, change or remove a download client.",
      ),
    ).toBeInTheDocument();
    expect(screen.getByTestId("download-client-test")).toBeEnabled();
  });

  it("cannot add a first download client", async () => {
    vi.spyOn(
      downloadClientsApi,
      "fetchDownloadClientConnections",
    ).mockResolvedValue([]);
    signedInAs(role);

    render(<DownloadClientsSection />, { wrapper });

    expect(
      await screen.findByRole("button", {
        name: "Add your first download client",
      }),
    ).toBeDisabled();
  });
});

describe("signed in as an admin", () => {
  it("can add, change and remove a media manager, with no note", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([MANAGER]);
    signedInAs("admin");

    render(<MediaManagersTab />, { wrapper });

    expect(await screen.findByTestId("media-manager-remove")).toBeEnabled();
    expect(screen.getByTestId("media-manager-edit")).toBeEnabled();
    expect(screen.getByTestId("media-manager-add")).toBeEnabled();
    expect(screen.queryByText(/Only an admin can/)).toBeNull();
  });

  it("can add, change and remove a download client, with no note", async () => {
    vi.spyOn(
      downloadClientsApi,
      "fetchDownloadClientConnections",
    ).mockResolvedValue([CLIENT]);
    signedInAs("admin");

    render(<DownloadClientsSection />, { wrapper });

    expect(await screen.findByTestId("download-client-remove")).toBeEnabled();
    expect(screen.getByTestId("download-client-edit")).toBeEnabled();
    expect(screen.getByTestId("download-client-add")).toBeEnabled();
    expect(screen.queryByText(/Only an admin can/)).toBeNull();
  });
});
