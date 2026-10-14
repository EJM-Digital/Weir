import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import * as api from "../../../../lib/media-managers/media-managers-api";
import type { MediaManagerConnection } from "../../../../lib/media-managers/media-managers-api";
import { AddConnectionForm } from "./add-connection-form";
import { ConnectionEditForm } from "./connection-edit-form";
import { MediaManagersTab } from "./media-managers-tab";
import { stubMediaManagersTabNeighbours } from "./stub-media-managers-tab-neighbours";

function connection(
  over: Partial<MediaManagerConnection> = {},
): MediaManagerConnection {
  return {
    id: 1,
    kind: "radarr",
    name: "Radarr on nas",
    enabled: true,
    base_url: "http://nas:7878",
    api_key_is_saved: true,
    webhook_secret_is_set: true,
    webhook_url_path: "/api/v1/intake/webhook/radarr",
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

function saveEdit(): void {
  fireEvent.click(screen.getByTestId("media-manager-edit-save"));
}

describe("a media manager's nickname", () => {
  it("is shown after the name Weir derives, on the connection's card", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection({ nickname: "4K" }),
    ]);

    render(<MediaManagersTab />, { wrapper });

    expect(
      await screen.findByRole("heading", { name: "Radarr on nas · 4K" }),
    ).toBeInTheDocument();
  });

  it("is used in the question before a connection is removed", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection({ nickname: "4K" }),
    ]);
    render(<MediaManagersTab />, { wrapper });

    fireEvent.click(await screen.findByTestId("media-manager-remove"));

    expect(
      await screen.findByRole("dialog", { name: "Remove Radarr on nas · 4K?" }),
    ).toBeInTheDocument();
  });

  it("leaves the name alone when there is none", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection({ nickname: null }),
    ]);

    render(<MediaManagersTab />, { wrapper });

    expect(
      await screen.findByRole("heading", { name: "Radarr on nas" }),
    ).toBeInTheDocument();
  });

  it("is offered as optional when adding, and sent trimmed", async () => {
    const create = vi
      .spyOn(api, "createMediaManagerConnection")
      .mockResolvedValue(connection());
    render(<AddConnectionForm onCancel={vi.fn()} onCreated={vi.fn()} />, {
      wrapper,
    });

    fireEvent.change(screen.getByTestId("media-manager-base-url"), {
      target: { value: "http://nas:7878" },
    });
    fireEvent.change(screen.getByLabelText("Nickname (optional)"), {
      target: { value: "  4K  " },
    });
    fireEvent.click(screen.getByTestId("media-manager-save"));

    await waitFor(() =>
      expect(create).toHaveBeenCalledWith(
        expect.objectContaining({ nickname: "4K" }),
      ),
    );
  });

  it("can be left blank when adding", async () => {
    const create = vi
      .spyOn(api, "createMediaManagerConnection")
      .mockResolvedValue(connection());
    render(<AddConnectionForm onCancel={vi.fn()} onCreated={vi.fn()} />, {
      wrapper,
    });

    fireEvent.change(screen.getByTestId("media-manager-base-url"), {
      target: { value: "http://nas:7878" },
    });
    fireEvent.click(screen.getByTestId("media-manager-save"));

    await waitFor(() =>
      expect(create).toHaveBeenCalledWith(
        expect.objectContaining({ nickname: "" }),
      ),
    );
  });

  it("stops at thirty characters in the form", () => {
    render(<AddConnectionForm onCancel={vi.fn()} onCreated={vi.fn()} />, {
      wrapper,
    });

    expect(screen.getByLabelText("Nickname (optional)")).toHaveAttribute(
      "maxlength",
      "30",
    );
  });

  it("starts an edit with the current nickname", () => {
    render(
      <ConnectionEditForm
        connection={connection({ nickname: "4K" })}
        onClose={vi.fn()}
      />,
      { wrapper },
    );

    expect(screen.getByLabelText("Nickname (optional)")).toHaveValue("4K");
  });

  it("is saved when changed while editing", async () => {
    const update = vi
      .spyOn(api, "updateMediaManagerConnection")
      .mockResolvedValue(connection({ nickname: "Kids" }));
    render(
      <ConnectionEditForm
        connection={connection({ nickname: "4K" })}
        onClose={vi.fn()}
      />,
      { wrapper },
    );

    fireEvent.change(screen.getByLabelText("Nickname (optional)"), {
      target: { value: "Kids" },
    });
    saveEdit();

    await waitFor(() =>
      expect(update).toHaveBeenCalledWith(1, {
        base_url: "http://nas:7878",
        downloaded_scan_enabled: false,
        nickname: "Kids",
      }),
    );
  });

  it("is cleared by emptying it while editing", async () => {
    const update = vi
      .spyOn(api, "updateMediaManagerConnection")
      .mockResolvedValue(connection());
    render(
      <ConnectionEditForm
        connection={connection({ nickname: "4K" })}
        onClose={vi.fn()}
      />,
      { wrapper },
    );

    fireEvent.change(screen.getByLabelText("Nickname (optional)"), {
      target: { value: "" },
    });
    saveEdit();

    await waitFor(() =>
      expect(update).toHaveBeenCalledWith(1, {
        base_url: "http://nas:7878",
        downloaded_scan_enabled: false,
        nickname: "",
      }),
    );
  });

  it("is left out of a save that did not touch it", async () => {
    const update = vi
      .spyOn(api, "updateMediaManagerConnection")
      .mockResolvedValue(connection());
    render(
      <ConnectionEditForm
        connection={connection({ nickname: "4K" })}
        onClose={vi.fn()}
      />,
      { wrapper },
    );

    fireEvent.change(screen.getByTestId("media-manager-edit-base-url"), {
      target: { value: "http://nas:7879" },
    });
    saveEdit();

    await waitFor(() =>
      expect(update).toHaveBeenCalledWith(1, {
        base_url: "http://nas:7879",
        downloaded_scan_enabled: false,
      }),
    );
  });
});

describe("the nickname field", () => {
  it("has no name field beside it: the name stays derived", () => {
    render(<AddConnectionForm onCancel={vi.fn()} onCreated={vi.fn()} />, {
      wrapper,
    });

    expect(screen.queryByLabelText("Name")).not.toBeInTheDocument();
    expect(screen.getByLabelText("Nickname (optional)")).toBeInTheDocument();
  });
});
