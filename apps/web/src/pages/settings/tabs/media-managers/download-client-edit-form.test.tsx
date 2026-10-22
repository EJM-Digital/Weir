import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { DownloadClientConnection } from "../../../../lib/download-clients/download-clients-api";
import { csrfToken } from "../../../../test/recorded-admin-session";
import {
  serveRecordedReplies,
  type RecordedReply,
} from "../../../../test/recorded-weir";
import { connectionList, sabnzbdKeyReplaced } from "./download-client-replies";
import { DownloadClientEditForm } from "./download-client-edit-form";

const SAVE_PATH = "/api/v1/download-clients/connections/1";
const CSRF = JSON.parse(csrfToken.body).csrf_token as string;

const [sabnzbd, qbittorrent, deluge] = JSON.parse(
  connectionList.body,
) as DownloadClientConnection[];

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function openForm(connection: DownloadClientConnection) {
  const onClose = vi.fn();
  render(<DownloadClientEditForm connection={connection} onClose={onClose} />, {
    wrapper,
  });
  return onClose;
}

function serve(
  save: RecordedReply | (() => Promise<RecordedReply>),
  path = SAVE_PATH,
) {
  return serveRecordedReplies({
    "GET /api/v1/auth/csrf": csrfToken,
    [`PUT ${path}`]: save,
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the API key of a SABnzbd download client", () => {
  it("is a masked field, empty, that says a blank keeps the current key", () => {
    openForm(sabnzbd);

    const key = screen.getByTestId("download-client-edit-api-key");
    expect(key).toHaveAttribute("type", "password");
    expect(key).toHaveValue("");
    expect(screen.getByLabelText("API key")).toBe(key);
    expect(
      screen.getByText("Leave blank to keep the current key."),
    ).toBeInTheDocument();
  });

  it("is not asked for by a client that signs in with a name and password", () => {
    openForm(qbittorrent);

    expect(
      screen.queryByTestId("download-client-edit-api-key"),
    ).not.toBeInTheDocument();
  });

  it("is left out of the save while it is blank, so the saved key stays", async () => {
    const weir = serve(sabnzbdKeyReplaced);
    const onClose = openForm(sabnzbd);

    fireEvent.change(screen.getByTestId("download-client-edit-base-url"), {
      target: { value: "http://localhost:18083" },
    });
    fireEvent.change(screen.getByTestId("download-client-edit-api-key"), {
      target: { value: "   " },
    });
    fireEvent.click(screen.getByTestId("download-client-edit-save"));

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(weir.sent("PUT", SAVE_PATH).map((r) => r.body)).toEqual([
      { base_url: "http://localhost:18083", csrf_token: CSRF },
    ]);
  });

  it("is sent, trimmed, once a new one is typed", async () => {
    const weir = serve(sabnzbdKeyReplaced);
    const onClose = openForm(sabnzbd);

    fireEvent.change(screen.getByTestId("download-client-edit-api-key"), {
      target: { value: " replacement-api-key " },
    });
    fireEvent.click(screen.getByTestId("download-client-edit-save"));

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(weir.sent("PUT", SAVE_PATH).map((r) => r.body)).toEqual([
      {
        base_url: "http://localhost:18080",
        api_key: "replacement-api-key",
        csrf_token: CSRF,
      },
    ]);
  });
});

describe("Cancel on a download client's edit form", () => {
  it("closes at once, sending nothing, when nothing was changed", () => {
    const weir = serve(sabnzbdKeyReplaced);
    const onClose = openForm(deluge);

    fireEvent.click(screen.getByTestId("download-client-edit-cancel"));

    expect(onClose).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId("settings-unsaved-changes")).toBeNull();
    expect(weir.requests).toEqual([]);
  });

  it("asks before dropping a changed field, and Keep editing leaves the form as it was", async () => {
    const weir = serve(sabnzbdKeyReplaced);
    const onClose = openForm(sabnzbd);

    fireEvent.change(screen.getByTestId("download-client-edit-api-key"), {
      target: { value: "replacement-api-key" },
    });
    fireEvent.click(screen.getByTestId("download-client-edit-cancel"));

    expect(
      await screen.findByTestId("settings-unsaved-changes"),
    ).toHaveTextContent(
      "You have unsaved changes to SABnzbd on localhost. Leave without saving?",
    );
    expect(onClose).not.toHaveBeenCalled();

    fireEvent.click(screen.getByTestId("settings-unsaved-changes-cancel"));

    await waitFor(() =>
      expect(screen.queryByTestId("settings-unsaved-changes")).toBeNull(),
    );
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByTestId("download-client-edit-api-key")).toHaveValue(
      "replacement-api-key",
    );
    expect(weir.requests).toEqual([]);
  });

  it("closes without sending anything once the person chooses to leave", async () => {
    const weir = serve(sabnzbdKeyReplaced);
    const onClose = openForm(sabnzbd);

    fireEvent.change(screen.getByTestId("download-client-edit-base-url"), {
      target: { value: "http://localhost:18083" },
    });
    fireEvent.click(screen.getByTestId("download-client-edit-cancel"));
    fireEvent.click(
      await screen.findByTestId("settings-unsaved-changes-confirm"),
    );

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(weir.requests).toEqual([]);
  });

  it("cannot be pressed while the save is in flight", async () => {
    let finish: (reply: RecordedReply) => void = () => undefined;
    serve(() => new Promise<RecordedReply>((resolve) => (finish = resolve)));
    const onClose = openForm(sabnzbd);

    fireEvent.change(screen.getByTestId("download-client-edit-api-key"), {
      target: { value: "replacement-api-key" },
    });
    fireEvent.click(screen.getByTestId("download-client-edit-save"));

    await waitFor(() =>
      expect(screen.getByTestId("download-client-edit-cancel")).toBeDisabled(),
    );
    finish(sabnzbdKeyReplaced);
    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
  });
});

const QBITTORRENT_PATH = `/api/v1/download-clients/connections/${qbittorrent.id}`;

describe("the sign-in details of a download client that uses a name and password", () => {
  it("sends the name, password and nickname that were typed, trimmed, and nothing that was left as it was", async () => {
    const weir = serve(sabnzbdKeyReplaced, QBITTORRENT_PATH);
    const onClose = openForm(qbittorrent);

    fireEvent.change(screen.getByTestId("download-client-edit-username"), {
      target: { value: "  alice " },
    });
    fireEvent.change(screen.getByTestId("download-client-edit-password"), {
      target: { value: " secret " },
    });
    fireEvent.change(screen.getByTestId("download-client-edit-nickname"), {
      target: { value: " Seedbox " },
    });
    fireEvent.click(screen.getByTestId("download-client-edit-save"));

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(weir.sent("PUT", QBITTORRENT_PATH).map((r) => r.body)).toEqual([
      {
        base_url: qbittorrent.base_url,
        username: "alice",
        password: "secret",
        nickname: "Seedbox",
        csrf_token: CSRF,
      },
    ]);
  });

  it("sends only the address when just the address changed", async () => {
    const weir = serve(sabnzbdKeyReplaced, QBITTORRENT_PATH);
    const onClose = openForm(qbittorrent);

    fireEvent.change(screen.getByTestId("download-client-edit-base-url"), {
      target: { value: "http://localhost:18999" },
    });
    fireEvent.click(screen.getByTestId("download-client-edit-save"));

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(weir.sent("PUT", QBITTORRENT_PATH).map((r) => r.body)).toEqual([
      { base_url: "http://localhost:18999", csrf_token: CSRF },
    ]);
  });
});
