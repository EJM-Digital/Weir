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

describe("creating a webhook secret", () => {
  it("says why a secret could not be created", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection({ webhook_secret_is_set: false }),
    ]);
    vi.spyOn(api, "generateMediaManagerWebhookSecret").mockRejectedValue(
      new Error("Could not reach the server."),
    );

    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByTestId("media-manager-generate-secret"));

    expect(
      await screen.findByText("Could not reach the server."),
    ).toBeInTheDocument();
  });

  it("creates a first secret without asking to confirm", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection({ webhook_secret_is_set: false }),
    ]);
    const generate = vi
      .spyOn(api, "generateMediaManagerWebhookSecret")
      .mockResolvedValue({
        connection_id: 1,
        webhook_secret: "first-secret",
        webhook_url_path: "/api/v1/intake/webhook/deluno",
        header_name: "X-Webhook-Secret",
      });

    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByTestId("media-manager-generate-secret"));

    await waitFor(() => expect(generate).toHaveBeenCalledTimes(1));
    expect(
      screen.queryByTestId("media-manager-replace-secret-confirm"),
    ).not.toBeInTheDocument();
  });

  it("asks before replacing a secret that is already set", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection({ name: "Deluno", webhook_secret_is_set: true }),
    ]);
    const generate = vi.spyOn(api, "generateMediaManagerWebhookSecret");

    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByTestId("media-manager-generate-secret"));

    const dialog = await screen.findByTestId(
      "media-manager-replace-secret-confirm",
    );
    expect(dialog).toHaveTextContent("Replace Deluno's secret?");
    expect(generate).not.toHaveBeenCalled();
  });

  it("replaces the secret only once the replacement is confirmed", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection({ webhook_secret_is_set: true }),
    ]);
    const generate = vi
      .spyOn(api, "generateMediaManagerWebhookSecret")
      .mockResolvedValue({
        connection_id: 1,
        webhook_secret: "new-secret",
        webhook_url_path: "/api/v1/intake/webhook/deluno",
        header_name: "X-Webhook-Secret",
      });

    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByTestId("media-manager-generate-secret"));
    fireEvent.click(
      screen.getByTestId("media-manager-replace-secret-confirm-confirm"),
    );

    await waitFor(() => expect(generate).toHaveBeenCalledWith(1));
    expect(await screen.findByTestId("media-manager-secret")).toHaveTextContent(
      "new-secret",
    );
  });
});
