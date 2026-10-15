import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { signedInAs } from "../../../../test/signed-in-as";
import * as api from "../../../../lib/download-clients/download-clients-api";
import type { DownloadClientConnection } from "../../../../lib/download-clients/download-clients-api";
import * as settingsQueries from "../../../../lib/settings/queries";
import { DownloadClientsSection } from "./download-clients-section";

function connection(
  over: Partial<DownloadClientConnection> = {},
): DownloadClientConnection {
  return {
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
    ...over,
  };
}

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
});

afterEach(() => {
  vi.restoreAllMocks();
});

async function openEditForm(existing: DownloadClientConnection) {
  vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([existing]);
  render(<DownloadClientsSection />, { wrapper });
  fireEvent.click(await screen.findByTestId("download-client-edit"));
  return screen.getByLabelText("Nickname (optional)");
}

describe("a download client's nickname", () => {
  it("is shown after the name Weir derives", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([
      connection({ nickname: "Seedbox" }),
    ]);
    render(<DownloadClientsSection />, { wrapper });

    expect(
      await screen.findByRole("heading", {
        name: "qBittorrent on 192.0.2.10 · Seedbox",
      }),
    ).toBeInTheDocument();
  });

  it("leaves the name alone when there is no nickname", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([
      connection({ nickname: null }),
    ]);
    render(<DownloadClientsSection />, { wrapper });

    expect(
      await screen.findByRole("heading", { name: "qBittorrent on 192.0.2.10" }),
    ).toBeInTheDocument();
  });

  it("is asked for as optional when adding, and sent trimmed", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([]);
    const create = vi
      .spyOn(api, "createDownloadClientConnection")
      .mockResolvedValue(connection());
    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-add"));
    fireEvent.change(screen.getByTestId("download-client-base-url"), {
      target: { value: "http://192.0.2.20:8080" },
    });
    fireEvent.change(screen.getByTestId("download-client-api-key"), {
      target: { value: "some-key" },
    });
    fireEvent.change(screen.getByLabelText("Nickname (optional)"), {
      target: { value: "  Usenet  " },
    });
    fireEvent.click(screen.getByTestId("download-client-save"));

    await waitFor(() =>
      expect(create).toHaveBeenCalledWith(
        expect.objectContaining({ nickname: "Usenet" }),
      ),
    );
  });

  it("stops at thirty characters in the form", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([]);
    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-add"));

    expect(screen.getByLabelText("Nickname (optional)")).toHaveAttribute(
      "maxlength",
      "30",
    );
  });

  it("starts an edit with the current nickname", async () => {
    const field = await openEditForm(connection({ nickname: "Seedbox" }));

    expect(field).toHaveValue("Seedbox");
  });

  it("is saved when changed while editing", async () => {
    const update = vi
      .spyOn(api, "updateDownloadClientConnection")
      .mockResolvedValue(connection({ nickname: "Usenet" }));
    const field = await openEditForm(connection({ nickname: "Seedbox" }));

    fireEvent.change(field, { target: { value: "Usenet" } });
    fireEvent.click(screen.getByTestId("download-client-edit-save"));

    await waitFor(() =>
      expect(update).toHaveBeenCalledWith(1, {
        base_url: "http://192.0.2.10:8080",
        nickname: "Usenet",
      }),
    );
  });

  it("is cleared by emptying it while editing", async () => {
    const update = vi
      .spyOn(api, "updateDownloadClientConnection")
      .mockResolvedValue(connection());
    const field = await openEditForm(connection({ nickname: "Seedbox" }));

    fireEvent.change(field, { target: { value: "  " } });
    fireEvent.click(screen.getByTestId("download-client-edit-save"));

    await waitFor(() =>
      expect(update).toHaveBeenCalledWith(1, {
        base_url: "http://192.0.2.10:8080",
        nickname: "",
      }),
    );
  });

  it("is left out of a save that did not touch it", async () => {
    const update = vi
      .spyOn(api, "updateDownloadClientConnection")
      .mockResolvedValue(connection());
    await openEditForm(connection({ nickname: "Seedbox" }));

    fireEvent.change(screen.getByTestId("download-client-edit-base-url"), {
      target: { value: "http://192.0.2.11:8080" },
    });
    fireEvent.click(screen.getByTestId("download-client-edit-save"));

    await waitFor(() =>
      expect(update).toHaveBeenCalledWith(1, {
        base_url: "http://192.0.2.11:8080",
      }),
    );
  });
});
