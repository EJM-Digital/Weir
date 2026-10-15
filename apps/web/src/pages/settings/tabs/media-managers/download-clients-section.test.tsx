import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

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

// Each card formats its last test time in the app timezone, which is read from the settings.
beforeEach(() => {
  vi.spyOn(settingsQueries, "useAppSettingsQuery").mockReturnValue({
    data: undefined,
  } as ReturnType<typeof settingsQueries.useAppSettingsQuery>);
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe("listing download client connections", () => {
  it("says nothing is connected when the list is empty", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([]);
    render(<DownloadClientsSection />, { wrapper });

    expect(
      await screen.findByText(/No download client is connected/i),
    ).toBeInTheDocument();
  });

  it("shows each connection with its kind and state", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([
      connection(),
      connection({
        id: 2,
        kind: "sabnzbd",
        name: "My SABnzbd",
        enabled: false,
      }),
    ]);
    render(<DownloadClientsSection />, { wrapper });

    expect(
      await screen.findByText("qBittorrent on 192.0.2.10"),
    ).toBeInTheDocument();
    expect(screen.getByText("My SABnzbd")).toBeInTheDocument();
    expect(screen.getAllByTestId("download-client-card")).toHaveLength(2);
  });

  it("shows a loading state before the connections arrive", () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockReturnValue(
      new Promise(() => {}),
    );
    render(<DownloadClientsSection />, { wrapper });

    expect(screen.getByText("Loading download clients")).toBeInTheDocument();
  });

  it("shows a plain error instead of the empty state when the load fails", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockRejectedValue(
      new Error("boom"),
    );
    render(<DownloadClientsSection />, { wrapper });

    expect(
      await screen.findByTestId("settings-load-error"),
    ).toBeInTheDocument();
    expect(
      screen.queryByText(/No download client is connected/i),
    ).not.toBeInTheDocument();
  });
});

describe("adding a download client", () => {
  it("only asks for the fields that kind's login actually uses", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([]);
    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-add"));

    // SABnzbd is the default selection: an API key field, no username/password.
    expect(screen.getByTestId("download-client-api-key")).toBeInTheDocument();
    expect(
      screen.queryByTestId("download-client-username"),
    ).not.toBeInTheDocument();

    fireEvent.change(screen.getByTestId("download-client-kind"), {
      target: { value: "deluge" },
    });
    expect(
      screen.queryByTestId("download-client-api-key"),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByTestId("download-client-username"),
    ).not.toBeInTheDocument();
    expect(screen.getByTestId("download-client-password")).toBeInTheDocument();

    fireEvent.change(screen.getByTestId("download-client-kind"), {
      target: { value: "qbittorrent" },
    });
    expect(screen.getByTestId("download-client-username")).toBeInTheDocument();
    expect(screen.getByTestId("download-client-password")).toBeInTheDocument();

    fireEvent.change(screen.getByTestId("download-client-kind"), {
      target: { value: "nzbget" },
    });
    expect(screen.getByTestId("download-client-username")).toBeInTheDocument();
    expect(screen.getByTestId("download-client-password")).toBeInTheDocument();
    expect(
      screen.queryByTestId("download-client-api-key"),
    ).not.toBeInTheDocument();

    fireEvent.change(screen.getByTestId("download-client-kind"), {
      target: { value: "transmission" },
    });
    expect(screen.getByTestId("download-client-username")).toBeInTheDocument();
    expect(screen.getByTestId("download-client-password")).toBeInTheDocument();
    expect(
      screen.queryByTestId("download-client-api-key"),
    ).not.toBeInTheDocument();
  });

  it("will not submit with no address", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([]);
    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-add"));

    expect(screen.getByTestId("download-client-save")).toBeDisabled();
  });

  it("asks for no name: the connection is named after its address", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([]);
    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-add"));

    expect(
      screen.queryByTestId("download-client-name"),
    ).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Name")).not.toBeInTheDocument();
  });

  it("will not submit a SABnzbd connection with no API key, or a Deluge one with no password", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([]);
    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-add"));
    fireEvent.change(screen.getByTestId("download-client-base-url"), {
      target: { value: "http://192.0.2.20:8080" },
    });

    // SABnzbd is the default selection: still blocked with no API key typed.
    expect(screen.getByTestId("download-client-save")).toBeDisabled();
    fireEvent.change(screen.getByTestId("download-client-api-key"), {
      target: { value: "some-key" },
    });
    expect(screen.getByTestId("download-client-save")).not.toBeDisabled();

    fireEvent.change(screen.getByTestId("download-client-kind"), {
      target: { value: "deluge" },
    });
    expect(screen.getByTestId("download-client-save")).toBeDisabled();
    fireEvent.change(screen.getByTestId("download-client-password"), {
      target: { value: "deluge-secret" },
    });
    expect(screen.getByTestId("download-client-save")).not.toBeDisabled();

    // Transmission can run unauthenticated, so it never needs a typed credential.
    fireEvent.change(screen.getByTestId("download-client-kind"), {
      target: { value: "transmission" },
    });
    expect(screen.getByTestId("download-client-save")).not.toBeDisabled();
  });

  it("adds a connection and says it will only be used for suggestions", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([]);
    const create = vi
      .spyOn(api, "createDownloadClientConnection")
      .mockResolvedValue(connection({ name: "SABnzbd on 192.0.2.20" }));

    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-add"));
    fireEvent.change(screen.getByTestId("download-client-base-url"), {
      target: { value: "http://192.0.2.20:8080" },
    });
    fireEvent.change(screen.getByTestId("download-client-api-key"), {
      target: { value: "some-key" },
    });
    fireEvent.click(screen.getByTestId("download-client-save"));

    await waitFor(() =>
      expect(create).toHaveBeenCalledWith(
        expect.objectContaining({
          kind: "sabnzbd",
          base_url: "http://192.0.2.20:8080",
          api_key: "some-key",
        }),
      ),
    );
    expect(create.mock.calls[0]?.[0]).not.toHaveProperty("name");
    expect(
      await screen.findByTestId("download-client-created-note"),
    ).toHaveTextContent("nothing is applied on its own");
  });

  it("saves only what the chosen kind's login uses, not what was typed for a kind the person moved away from", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([]);
    const create = vi
      .spyOn(api, "createDownloadClientConnection")
      .mockResolvedValue(connection({ name: "SABnzbd on 192.0.2.20" }));

    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-add"));
    fireEvent.change(screen.getByTestId("download-client-kind"), {
      target: { value: "qbittorrent" },
    });
    fireEvent.change(screen.getByTestId("download-client-username"), {
      target: { value: "someone" },
    });
    fireEvent.change(screen.getByTestId("download-client-password"), {
      target: { value: "typed-for-qbittorrent" },
    });
    fireEvent.change(screen.getByTestId("download-client-kind"), {
      target: { value: "sabnzbd" },
    });
    fireEvent.change(screen.getByTestId("download-client-base-url"), {
      target: { value: "http://192.0.2.20:8080" },
    });
    fireEvent.change(screen.getByTestId("download-client-api-key"), {
      target: { value: "some-key" },
    });
    fireEvent.click(screen.getByTestId("download-client-save"));

    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    const sent = create.mock.calls[0]?.[0];
    expect(sent).toMatchObject({
      kind: "sabnzbd",
      api_key: "some-key",
      username: "",
      password: "",
    });
  });

  it("says why adding a connection failed", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([]);
    vi.spyOn(api, "createDownloadClientConnection").mockRejectedValue(
      new Error("Could not reach the server."),
    );

    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-add"));
    fireEvent.change(screen.getByTestId("download-client-base-url"), {
      target: { value: "http://192.0.2.20:8080" },
    });
    fireEvent.change(screen.getByTestId("download-client-api-key"), {
      target: { value: "some-key" },
    });
    fireEvent.click(screen.getByTestId("download-client-save"));

    expect(
      await screen.findByText("Could not reach the server."),
    ).toBeInTheDocument();
  });
});

describe("testing a connection", () => {
  it("shows pending, then success", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([
      connection(),
    ]);
    let finishTest!: (value: api.DownloadClientConnectionTest) => void;
    const pending = new Promise<api.DownloadClientConnectionTest>((resolve) => {
      finishTest = resolve;
    });
    vi.spyOn(api, "testDownloadClientConnection").mockReturnValue(pending);

    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-test"));

    expect(
      await screen.findByRole("button", { name: "Testing…" }),
    ).toBeInTheDocument();

    finishTest({
      connection_id: 1,
      ok: true,
      detail: "Connected. Weir can reach qBittorrent on 192.0.2.10.",
      checked_at: "2026-09-24T10:00:00Z",
    });
    await waitFor(() =>
      expect(screen.getByTestId("download-client-test")).toBeEnabled(),
    );
  });

  it("says why a test failed", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([
      connection(),
    ]);
    vi.spyOn(api, "testDownloadClientConnection").mockRejectedValue(
      new Error("Could not reach the server."),
    );

    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-test"));

    expect(
      await screen.findByText("Could not reach the server."),
    ).toBeInTheDocument();
  });

  it("never says Answering with no check time behind it", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([
      connection({ last_test_ok: true, last_test_at: null }),
    ]);
    render(<DownloadClientsSection />, { wrapper });

    const status = await screen.findByTestId("download-client-status");
    expect(status).toHaveTextContent("Not tested yet");
    expect(status).not.toHaveTextContent("Answering");
  });

  it("shows the failure detail once checked", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([
      connection({
        last_test_ok: false,
        last_test_at: "2026-09-24T10:00:00Z",
        last_test_detail: "Weir could not reach qBittorrent.",
      }),
    ]);
    render(<DownloadClientsSection />, { wrapper });

    const status = await screen.findByTestId("download-client-status");
    expect(status).toHaveTextContent("Not answering");
    expect(status).toHaveTextContent("Weir could not reach qBittorrent.");
  });
});

describe("editing a connection in place", () => {
  it("opens with the current address and username, blank secrets, and no field for a name", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([
      connection(),
    ]);
    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-edit"));

    expect(
      screen.queryByTestId("download-client-edit-name"),
    ).not.toBeInTheDocument();
    expect(screen.getByTestId("download-client-edit-base-url")).toHaveValue(
      "http://192.0.2.10:8080",
    );
    expect(screen.getByTestId("download-client-edit-username")).toHaveValue(
      "admin",
    );
    expect(screen.getByTestId("download-client-edit-password")).toHaveValue("");
  });

  it("saves only the changed fields", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([
      connection(),
    ]);
    const update = vi
      .spyOn(api, "updateDownloadClientConnection")
      .mockResolvedValue(connection({ name: "qBittorrent on 192.0.2.11" }));

    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-edit"));
    fireEvent.change(screen.getByTestId("download-client-edit-base-url"), {
      target: { value: "http://192.0.2.11:8080" },
    });
    fireEvent.click(screen.getByTestId("download-client-edit-save"));

    await waitFor(() =>
      expect(update).toHaveBeenCalledWith(1, {
        base_url: "http://192.0.2.11:8080",
      }),
    );
    await waitFor(() =>
      expect(
        screen.queryByTestId("download-client-edit-form"),
      ).not.toBeInTheDocument(),
    );
  });
});

describe("removing a connection", () => {
  it("does not delete anything on the first click", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([
      connection(),
    ]);
    const del = vi
      .spyOn(api, "deleteDownloadClientConnection")
      .mockResolvedValue(undefined);

    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-remove"));

    expect(
      await screen.findByTestId("download-client-remove-confirm"),
    ).toBeInTheDocument();
    expect(del).not.toHaveBeenCalled();
    expect(screen.getByText("qBittorrent on 192.0.2.10")).toBeInTheDocument();
  });

  it("names the connection it is about to remove", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([
      connection(),
    ]);
    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-remove"));

    expect(
      screen.getByTestId("download-client-remove-confirm"),
    ).toHaveTextContent("Remove qBittorrent on 192.0.2.10?");
  });

  it("removes only after the dialog is confirmed", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([
      connection(),
    ]);
    const del = vi
      .spyOn(api, "deleteDownloadClientConnection")
      .mockResolvedValue(undefined);

    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-remove"));
    fireEvent.click(
      screen.getByTestId("download-client-remove-confirm-confirm"),
    );

    await waitFor(() => expect(del).toHaveBeenCalledTimes(1));
    expect(del).toHaveBeenCalledWith(1);
  });

  it("leaves the connection alone when cancelled", async () => {
    vi.spyOn(api, "fetchDownloadClientConnections").mockResolvedValue([
      connection(),
    ]);
    const del = vi
      .spyOn(api, "deleteDownloadClientConnection")
      .mockResolvedValue(undefined);

    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-remove"));
    fireEvent.click(
      screen.getByTestId("download-client-remove-confirm-cancel"),
    );

    await waitFor(() =>
      expect(
        screen.queryByTestId("download-client-remove-confirm"),
      ).not.toBeInTheDocument(),
    );
    expect(del).not.toHaveBeenCalled();
  });
});

describe("turning a connection on or off", () => {
  it("shows a pending label while the change is in flight", async () => {
    const fetchConnections = vi.spyOn(api, "fetchDownloadClientConnections");
    fetchConnections.mockResolvedValueOnce([connection({ enabled: true })]);
    fetchConnections.mockResolvedValue([connection({ enabled: false })]);
    let finishUpdate!: (value: DownloadClientConnection) => void;
    const pending = new Promise<DownloadClientConnection>((resolve) => {
      finishUpdate = resolve;
    });
    vi.spyOn(api, "updateDownloadClientConnection").mockReturnValue(pending);

    render(<DownloadClientsSection />, { wrapper });
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
});
