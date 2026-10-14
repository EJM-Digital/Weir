import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import * as api from "../../../../lib/media-managers/media-managers-api";
import type { MediaManagerConnection } from "../../../../lib/media-managers/media-managers-api";
import { MediaManagersTab } from "./media-managers-tab";
import { stubMediaManagersTabNeighbours } from "./stub-media-managers-tab-neighbours";

function connection(
  over: Partial<MediaManagerConnection> = {},
): MediaManagerConnection {
  return {
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
    ...over,
  };
}

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

describe("testing a connection", () => {
  it("says why a test could not run", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection(),
    ]);
    vi.spyOn(api, "testMediaManagerConnection").mockRejectedValue(
      new Error("Could not reach the server."),
    );

    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByTestId("media-manager-test"));

    expect(
      await screen.findByText("Could not reach the server."),
    ).toBeInTheDocument();
  });
});

describe("turning a media manager on or off", () => {
  it("shows a pending label while the change is in flight", async () => {
    const fetchConnections = vi.spyOn(api, "fetchMediaManagerConnections");
    fetchConnections.mockResolvedValueOnce([connection({ enabled: true })]);
    fetchConnections.mockResolvedValue([connection({ enabled: false })]);
    let finishUpdate!: (value: MediaManagerConnection) => void;
    const pending = new Promise<MediaManagerConnection>((resolve) => {
      finishUpdate = resolve;
    });
    vi.spyOn(api, "updateMediaManagerConnection").mockReturnValue(pending);

    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByRole("button", { name: "Disable" }));

    expect(
      await screen.findByRole("button", { name: "Disabling…" }),
    ).toBeInTheDocument();

    finishUpdate(connection({ enabled: false }));
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Enable" }),
      ).toBeInTheDocument(),
    );
  });

  it("says why the change failed", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection(),
    ]);
    vi.spyOn(api, "updateMediaManagerConnection").mockRejectedValue(
      new Error("Could not reach the server."),
    );

    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByRole("button", { name: "Disable" }));

    expect(
      await screen.findByText("Could not reach the server."),
    ).toBeInTheDocument();
  });
});

describe("the unsigned webhook warning", () => {
  it("names the manager in the warning, once the field is set", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection({ name: "Deluno", unsigned_webhook_warning: "unsigned" }),
    ]);
    render(<MediaManagersTab />, { wrapper });

    expect(
      await screen.findByTestId("media-manager-unsigned-webhook-warning"),
    ).toHaveTextContent(
      'Deluno accepts webhooks without a secret. Create one under "How to point it at Weir", then add it to Deluno.',
    );
  });

  it("says nothing once a secret protects the webhook", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection({ unsigned_webhook_warning: null }),
    ]);
    render(<MediaManagersTab />, { wrapper });
    await screen.findByTestId("media-manager-card");

    expect(
      screen.queryByTestId("media-manager-unsigned-webhook-warning"),
    ).not.toBeInTheDocument();
  });
});

describe("editing a media manager in place", () => {
  it("opens with the current address and a blank api key, and no field for a name", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection({ name: "Deluno", base_url: "http://192.0.2.10:5099" }),
    ]);
    render(<MediaManagersTab />, { wrapper });

    fireEvent.click(await screen.findByTestId("media-manager-edit"));

    expect(
      screen.queryByTestId("media-manager-edit-name"),
    ).not.toBeInTheDocument();
    expect(screen.getByTestId("media-manager-edit-base-url")).toHaveValue(
      "http://192.0.2.10:5099",
    );
    expect(screen.getByTestId("media-manager-edit-api-key")).toHaveValue("");
  });

  it("saves the edited fields and closes the form", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection(),
    ]);
    const update = vi
      .spyOn(api, "updateMediaManagerConnection")
      .mockResolvedValue(connection({ name: "Deluno on 192.0.2.11" }));

    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByTestId("media-manager-edit"));
    fireEvent.change(screen.getByTestId("media-manager-edit-base-url"), {
      target: { value: "http://192.0.2.11:5099" },
    });
    fireEvent.click(screen.getByTestId("media-manager-edit-save"));

    await waitFor(() =>
      expect(update).toHaveBeenCalledWith(1, {
        base_url: "http://192.0.2.11:5099",
      }),
    );
    await waitFor(() =>
      expect(
        screen.queryByTestId("media-manager-edit-form"),
      ).not.toBeInTheDocument(),
    );
  });
});
