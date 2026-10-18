import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { DownloadClientConnection } from "../../../../lib/download-clients/download-clients-api";
import { heldReply } from "../../../../test/held-reply";
import { adminMe, csrfToken } from "../../../../test/recorded-admin-session";
import { recordedReply } from "../../../../test/recorded-reply";
import { systemReplies } from "../../../../test/recorded-system";
import {
  serveRecordedReplies,
  type RecordedReply,
  type RecordedRoutes,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import { connectionList } from "./download-client-replies";
import { noConnections } from "./connection-replies";
import { DownloadClientsSection } from "./download-clients-section";

const CONNECTIONS = "/api/v1/download-clients/connections";
const ADD_PANEL = "download-client-add-panel";
const REMOVE_DIALOG = "download-client-remove-confirm";

const recordedAdded = recordedReply(`POST ${CONNECTIONS}`);
const recordedSaved = recordedReply(`PUT ${CONNECTIONS}/1`);
const recordedRemoved = recordedReply(`DELETE ${CONNECTIONS}/1`);

const [sabnzbd, qbittorrent, deluge] = JSON.parse(
  connectionList.body,
) as DownloadClientConnection[];

function listOf(...connections: DownloadClientConnection[]): RecordedReply {
  return { status: 200, body: JSON.stringify(connections) };
}

const unreachable = () => Promise.reject(new TypeError("Failed to fetch"));

let weir: RecordedWeir | undefined;

afterEach(() => {
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  vi.unstubAllGlobals();
});

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function serve(routes: RecordedRoutes): RecordedWeir {
  weir = serveRecordedReplies({
    "GET /api/v1/auth/csrf": csrfToken,
    "GET /api/v1/auth/me": adminMe,
    "GET /api/v1/suite/settings": systemReplies.settings,
    ...routes,
  });
  return weir;
}

async function openAddDrawer(): Promise<HTMLElement> {
  fireEvent.click(await screen.findByTestId("download-client-add"));
  return screen.getByTestId(ADD_PANEL);
}

function chooseKind(kind: string): void {
  fireEvent.change(screen.getByTestId("download-client-kind"), {
    target: { value: kind },
  });
}

describe("the first download client", () => {
  it("is added from the button in the empty state, which opens the add drawer", async () => {
    serve({ [`GET ${CONNECTIONS}`]: noConnections });
    render(<DownloadClientsSection />, { wrapper });

    fireEvent.click(
      await screen.findByRole("button", {
        name: "Add your first download client",
      }),
    );

    expect(screen.getByTestId(ADD_PANEL)).toBeInTheDocument();
    expect(screen.getByTestId("download-client-kind")).toHaveFocus();
  });
});

describe("the add drawer", () => {
  it.each([
    ["sabnzbd", ["download-client-api-key"]],
    ["nzbget", ["download-client-password"]],
    ["qbittorrent", ["download-client-password"]],
    ["deluge", ["download-client-password"]],
    ["transmission", ["download-client-password"]],
  ])("masks what a %s login keeps secret", async (kind, secrets) => {
    serve({ [`GET ${CONNECTIONS}`]: noConnections });
    render(<DownloadClientsSection />, { wrapper });
    await openAddDrawer();

    chooseKind(kind);

    for (const testId of secrets) {
      expect(screen.getByTestId(testId)).toHaveAttribute("type", "password");
    }
    const username = screen.queryByTestId("download-client-username");
    if (username) expect(username).not.toHaveAttribute("type", "password");
  });

  it("says Adding… and cannot be submitted again while the server answers", async () => {
    const answer = heldReply(recordedAdded);
    const server = serve({
      [`GET ${CONNECTIONS}`]: [noConnections, listOf(sabnzbd)],
      [`POST ${CONNECTIONS}`]: answer.route,
    });
    render(<DownloadClientsSection />, { wrapper });
    await openAddDrawer();
    fireEvent.change(screen.getByTestId("download-client-base-url"), {
      target: { value: "http://localhost:8085" },
    });
    fireEvent.change(screen.getByTestId("download-client-api-key"), {
      target: { value: "typed-api-key" },
    });

    fireEvent.click(screen.getByTestId("download-client-save"));
    const pending = await screen.findByRole("button", { name: "Adding…" });
    fireEvent.click(pending);

    expect(pending).toBeDisabled();
    expect(server.sent("POST", CONNECTIONS)).toHaveLength(1);
    answer.release();
    await waitFor(() =>
      expect(screen.queryByTestId(ADD_PANEL)).not.toBeInTheDocument(),
    );
  });

  const ways: [string, () => void][] = [
    [
      "Cancel",
      () =>
        fireEvent.click(
          within(screen.getByTestId(ADD_PANEL)).getByRole("button", {
            name: "Cancel",
          }),
        ),
    ],
    [
      "the x",
      () =>
        fireEvent.click(
          within(screen.getByTestId(ADD_PANEL)).getByRole("button", {
            name: "Close",
          }),
        ),
    ],
    ["Escape", () => fireEvent.keyDown(document, { key: "Escape" })],
    [
      "the backdrop",
      () => {
        const backdrop = document.querySelector(".mm-drawer-backdrop");
        if (!backdrop) throw new Error("no backdrop");
        fireEvent.click(backdrop);
      },
    ],
  ];

  it.each(ways)("is closed by %s without adding anything", async (_, close) => {
    const server = serve({ [`GET ${CONNECTIONS}`]: noConnections });
    render(<DownloadClientsSection />, { wrapper });
    await openAddDrawer();
    fireEvent.change(screen.getByTestId("download-client-base-url"), {
      target: { value: "http://localhost:8085" },
    });

    close();

    expect(screen.queryByTestId(ADD_PANEL)).not.toBeInTheDocument();
    expect(server.sent("POST", CONNECTIONS)).toEqual([]);
  });
});

describe("turning a download client on or off", () => {
  it("says why the change failed and leaves the button as it was", async () => {
    serve({
      [`GET ${CONNECTIONS}`]: listOf(sabnzbd),
      [`PUT ${CONNECTIONS}/1`]: unreachable,
    });
    render(<DownloadClientsSection />, { wrapper });

    fireEvent.click(await screen.findByRole("button", { name: "Disable" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Can't reach Weir. Check it's still running.",
    );
    expect(screen.getByRole("button", { name: "Disable" })).toBeEnabled();
  });
});

describe("editing a download client", () => {
  async function openEdit(connection: DownloadClientConnection, routes = {}) {
    const server = serve({
      [`GET ${CONNECTIONS}`]: listOf(connection),
      ...routes,
    });
    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-edit"));
    return server;
  }

  const username = () => screen.queryByTestId("download-client-edit-username");
  const password = () => screen.queryByTestId("download-client-edit-password");
  const apiKey = () => screen.queryByTestId("download-client-edit-api-key");

  it("asks SABnzbd for its API key and nothing else", async () => {
    await openEdit(sabnzbd);

    expect(apiKey()).toHaveAttribute("type", "password");
    expect(username()).toBeNull();
    expect(password()).toBeNull();
  });

  it("asks Deluge for its password only", async () => {
    await openEdit(deluge);

    expect(password()).toHaveAttribute("type", "password");
    expect(username()).toBeNull();
    expect(apiKey()).toBeNull();
  });

  it.each(["qbittorrent", "nzbget", "transmission"] as const)(
    "asks %s for a username and a password",
    async (kind) => {
      await openEdit({ ...qbittorrent, kind });

      expect(username()).toHaveValue("admin");
      expect(password()).toHaveAttribute("type", "password");
      expect(apiKey()).toBeNull();
    },
  );

  it("cannot be saved with the address blank, and says nothing is sent", async () => {
    const server = await openEdit(sabnzbd);

    fireEvent.change(screen.getByTestId("download-client-edit-base-url"), {
      target: { value: "  " },
    });

    expect(screen.getByTestId("download-client-edit-save")).toBeDisabled();
    expect(server.sent("PUT", `${CONNECTIONS}/1`)).toEqual([]);
  });

  it("says Saving… and holds Save still while the server answers", async () => {
    const answer = heldReply(recordedSaved);
    const server = await openEdit(sabnzbd, {
      [`PUT ${CONNECTIONS}/1`]: answer.route,
    });
    fireEvent.change(screen.getByTestId("download-client-edit-base-url"), {
      target: { value: "http://localhost:8086" },
    });

    fireEvent.click(screen.getByTestId("download-client-edit-save"));

    const saving = await screen.findByRole("button", { name: "Saving…" });
    expect(saving).toBeDisabled();
    fireEvent.click(saving);
    expect(server.sent("PUT", `${CONNECTIONS}/1`)).toHaveLength(1);
    answer.release();
    await waitFor(() =>
      expect(
        screen.queryByTestId("download-client-edit-form"),
      ).not.toBeInTheDocument(),
    );
  });
});

describe("removing a download client", () => {
  async function openRemove(routes: RecordedRoutes = {}) {
    const server = serve({
      [`GET ${CONNECTIONS}`]: [listOf(sabnzbd), listOf()],
      ...routes,
    });
    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-remove"));
    await screen.findByTestId(REMOVE_DIALOG);
    return server;
  }

  it("opens with the focus on the safe choice, and Escape keeps the download client", async () => {
    const server = await openRemove();

    expect(screen.getByTestId(`${REMOVE_DIALOG}-cancel`)).toHaveFocus();
    fireEvent.keyDown(document, { key: "Escape" });

    expect(screen.queryByTestId(REMOVE_DIALOG)).not.toBeInTheDocument();
    expect(screen.getByTestId("download-client-card")).toBeInTheDocument();
    expect(server.sent("DELETE", `${CONNECTIONS}/1`)).toEqual([]);
  });

  it("stays open and says why when the removal fails", async () => {
    await openRemove({ [`DELETE ${CONNECTIONS}/1`]: unreachable });

    fireEvent.click(screen.getByTestId(`${REMOVE_DIALOG}-confirm`));

    expect(
      await within(screen.getByTestId(REMOVE_DIALOG)).findByRole("alert"),
    ).toHaveTextContent("Can't reach Weir. Check it's still running.");
    expect(screen.getByTestId("download-client-card")).toBeInTheDocument();
  });

  it("closes once the download client is removed, and its card goes", async () => {
    await openRemove({ [`DELETE ${CONNECTIONS}/1`]: recordedRemoved });

    fireEvent.click(screen.getByTestId(`${REMOVE_DIALOG}-confirm`));

    await waitFor(() =>
      expect(screen.queryByTestId(REMOVE_DIALOG)).not.toBeInTheDocument(),
    );
    await waitFor(() =>
      expect(
        screen.queryByTestId("download-client-card"),
      ).not.toBeInTheDocument(),
    );
    expect(screen.getByTestId("download-clients-empty")).toBeInTheDocument();
  });
});
