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

describe("SettingsMediaManagersTab", () => {
  it("says plainly when nothing is configured, because nothing will reach Weir", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([]);
    render(<MediaManagersTab />, { wrapper });

    expect(
      await screen.findByText(/Nothing is connected yet/i),
    ).toBeInTheDocument();
  });

  it("warns once, naming every manager that has no secret, and flags each one's setup", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection({ id: 1, name: "Radarr", unsigned_webhook_warning: "x" }),
      connection({ id: 2, name: "Sonarr", unsigned_webhook_warning: "x" }),
      connection({ id: 3, name: "Deluno", webhook_secret_is_set: true }),
    ]);
    render(<MediaManagersTab />, { wrapper });

    const warning = await screen.findByTestId(
      "media-manager-unsigned-webhook-warning",
    );
    expect(warning).toHaveTextContent(
      "Radarr and Sonarr accept webhooks without a secret.",
    );
    expect(
      screen.getAllByTestId("media-manager-unsigned-webhook-warning"),
    ).toHaveLength(1);
    expect(screen.getAllByText("Needs a secret")).toHaveLength(2);
  });

  it("says a switched off manager is off, not what it last answered", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection({
        enabled: false,
        last_test_ok: true,
        last_test_at: "2026-08-26T10:00:00Z",
      }),
    ]);
    render(<MediaManagersTab />, { wrapper });

    const status = await screen.findByTestId("media-manager-status");
    expect(status).toHaveTextContent("Off");
    expect(status).not.toHaveTextContent("Answering");
  });

  it("shows each manager with the address it should post to", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection(),
      connection({
        id: 2,
        kind: "radarr",
        name: "Radarr",
        webhook_url_path: "/api/v1/intake/webhook/radarr",
      }),
    ]);
    render(<MediaManagersTab />, { wrapper });

    expect(await screen.findByText("Deluno")).toBeInTheDocument();
    expect(screen.getByText("Radarr")).toBeInTheDocument();

    // The card shows a full URL, not the bare path the API returns, because the
    // operator has to paste it into another app on another machine.
    const urls = screen
      .getAllByTestId("media-manager-webhook-url")
      .map((el) => el.textContent);
    expect(urls.some((u) => u?.endsWith("/api/v1/intake/webhook/deluno"))).toBe(
      true,
    );
    expect(urls.some((u) => u?.endsWith("/api/v1/intake/webhook/radarr"))).toBe(
      true,
    );
    expect(urls.every((u) => u?.startsWith("http"))).toBe(true);
  });

  it("warns when a manager has no secret, since anyone could post as it", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection({ webhook_secret_is_set: false }),
    ]);
    render(<MediaManagersTab />, { wrapper });

    expect(await screen.findByText(/no secret yet/i)).toBeInTheDocument();
  });

  it("shows a generated secret once, and says that is the only time", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection(),
    ]);
    vi.spyOn(api, "generateMediaManagerWebhookSecret").mockResolvedValue({
      connection_id: 1,
      webhook_secret: "s3cr3t-value",
      webhook_url_path: "/api/v1/intake/webhook/deluno",
      header_name: "X-Webhook-Secret",
    });

    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByTestId("media-manager-generate-secret"));

    await waitFor(() =>
      expect(screen.getByTestId("media-manager-secret")).toHaveTextContent(
        "s3cr3t-value",
      ),
    );
    expect(screen.getByText(/will not show it again/i)).toBeInTheDocument();
  });

  it("says Answering, not what the endpoint replied", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection({
        last_test_ok: true,
        last_test_at: "2026-08-26T10:00:00Z",
        last_test_detail: "Connected. Weir can reach Deluno.",
      }),
    ]);
    render(<MediaManagersTab />, { wrapper });

    const status = await screen.findByTestId("media-manager-status");
    expect(status).toHaveTextContent("Answering");
    // The headline already says it. Repeating the backend's sentence underneath
    // would be the same fact twice.
    expect(status).not.toHaveTextContent("Weir can reach");
  });

  it("shows why a failed test failed, because that is the actionable part", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection({
        last_test_ok: false,
        last_test_at: "2026-08-26T10:00:00Z",
        last_test_detail:
          "Weir reached Deluno, but the API key was refused. Check the key and save it again.",
      }),
    ]);
    render(<MediaManagersTab />, { wrapper });

    const status = await screen.findByTestId("media-manager-status");
    expect(status).toHaveTextContent("Not answering");
    expect(status).toHaveTextContent(/API key was refused/i);
  });

  it("says it has not been checked rather than implying a result", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection({ last_test_ok: null, last_test_at: null }),
    ]);
    render(<MediaManagersTab />, { wrapper });

    const status = await screen.findByTestId("media-manager-status");
    expect(status).toHaveTextContent("Checking…");
    expect(status).toHaveTextContent("never");
  });

  it("never says Answering when there is no check time behind it", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection({
        last_test_ok: true,
        last_test_at: null,
        last_test_detail: "Reachable, 214 series",
      }),
    ]);
    render(<MediaManagersTab />, { wrapper });

    const status = await screen.findByTestId("media-manager-status");
    // "Connected" above "Last checked: never" is two statements that cannot both
    // be true. Without a time, nothing has established the connection.
    expect(status).not.toHaveTextContent("Answering");
    expect(status).toHaveTextContent("Checking…");
    expect(status).toHaveTextContent("never");
    expect(status.querySelector(".mm-status-text--healthy")).toBeNull();
  });

  it("prevents remove or update while a connection test is running", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection(),
    ]);
    let finishTest!: (value: api.MediaManagerConnectionTest) => void;
    const pendingTest = new Promise<api.MediaManagerConnectionTest>(
      (resolve) => {
        finishTest = resolve;
      },
    );
    vi.spyOn(api, "testMediaManagerConnection").mockReturnValue(pendingTest);

    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByTestId("media-manager-test"));

    await waitFor(() =>
      expect(screen.getByTestId("media-manager-test")).toBeDisabled(),
    );
    expect(screen.getByTestId("media-manager-remove")).toBeDisabled();
    expect(screen.getByRole("button", { name: "Disable" })).toBeDisabled();

    finishTest({
      connection_id: 1,
      ok: false,
      detail: "Could not reach Deluno.",
      checked_at: "2026-07-28T07:00:00Z",
    });
    await waitFor(() =>
      expect(screen.getByTestId("media-manager-remove")).toBeEnabled(),
    );
  });

  it("keeps the address and secret folded away behind a disclosure", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection(),
    ]);
    render(<MediaManagersTab />, { wrapper });

    // The card answers "is it connected" first; wiring details are one click away.
    const details = await screen.findByTestId("media-manager-setup-details");
    expect(details.tagName.toLowerCase()).toBe("details");
    expect(details).not.toHaveAttribute("open");
    // The summary says it opens, since the browser's own triangle is hidden.
    const summary = details.querySelector("summary");
    expect(summary).toHaveTextContent("How to point Deluno at Weir");
    expect(summary).toHaveTextContent("Show →");
  });

  it("points Sonarr and Radarr at the library editor's mapping, not at Deluno's hand-off", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
      connection({ id: 2, kind: "sonarr", name: "Sonarr" }),
      connection(),
    ]);
    render(<MediaManagersTab />, { wrapper });

    const pointer = await screen.findByTestId("media-manager-mapping-pointer");
    expect(pointer).toHaveTextContent(
      "Sonarr picks up what Weir cleans through a remote path mapping",
    );
    expect(screen.getAllByTestId("media-manager-mapping-pointer")).toHaveLength(
      1,
    );
  });

  it("does not name internal modules in the intro", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([]);
    const { container } = render(<MediaManagersTab />, { wrapper });
    await screen.findByText(/Nothing is connected yet/i);

    // The intro says what this screen is for, not how every app and Processing fit together.
    const text = container.textContent ?? "";
    expect(text).not.toContain("Processing");
  });

  it("adds a manager of a kind that never had columns of its own", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([]);
    const create = vi
      .spyOn(api, "createMediaManagerConnection")
      .mockResolvedValue(connection());

    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByTestId("media-manager-add"));

    fireEvent.change(screen.getByTestId("media-manager-base-url"), {
      target: { value: "http://192.0.2.10:5099" },
    });
    fireEvent.click(screen.getByTestId("media-manager-save"));

    await waitFor(() =>
      expect(create).toHaveBeenCalledWith(
        expect.objectContaining({
          kind: "deluno",
          base_url: "http://192.0.2.10:5099",
        }),
      ),
    );
    expect(create.mock.calls[0]?.[0]).not.toHaveProperty("name");
  });

  it("opens the add form in a drawer with focus on its first field, and keeps the Add button", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([]);
    render(<MediaManagersTab />, { wrapper });

    fireEvent.click(await screen.findByTestId("media-manager-add"));

    const panel = screen.getByRole("dialog", { name: "Add a media manager" });
    expect(panel).toContainElement(screen.getByTestId("media-manager-kind"));
    expect(screen.getByTestId("media-manager-kind")).toHaveFocus();
    expect(screen.getByTestId("media-manager-add")).toBeInTheDocument();
  });

  it("puts the add form away without adding anything when it is cancelled", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([]);
    const create = vi.spyOn(api, "createMediaManagerConnection");
    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByTestId("media-manager-add"));

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(create).not.toHaveBeenCalled();
  });

  it("asks for no name: the connection is named after its address", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([]);
    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByTestId("media-manager-add"));

    expect(screen.queryByTestId("media-manager-name")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Name")).not.toBeInTheDocument();
  });

  it("will not submit a manager with no address", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([]);
    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByTestId("media-manager-add"));

    expect(screen.getByTestId("media-manager-save")).toBeDisabled();
  });

  it.each(["radarr", "sonarr", "deluno"])(
    "will not submit a %s connection with no address",
    async (kind) => {
      vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([]);
      render(<MediaManagersTab />, { wrapper });
      fireEvent.click(await screen.findByTestId("media-manager-add"));

      fireEvent.change(screen.getByTestId("media-manager-kind"), {
        target: { value: kind },
      });
      fireEvent.change(screen.getByTestId("media-manager-base-url"), {
        target: { value: "   " },
      });

      expect(screen.getByTestId("media-manager-save")).toBeDisabled();
    },
  );

  it("adds a Something else manager with no address and says the address is optional", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([]);
    const create = vi
      .spyOn(api, "createMediaManagerConnection")
      .mockResolvedValue(connection({ kind: "native", base_url: "" }));
    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByTestId("media-manager-add"));

    fireEvent.change(screen.getByTestId("media-manager-kind"), {
      target: { value: "native" },
    });

    expect(screen.getByText(/^Optional\. Leave it blank/)).toBeInTheDocument();
    expect(screen.getByTestId("media-manager-save")).toBeEnabled();
    fireEvent.click(screen.getByTestId("media-manager-save"));
    await waitFor(() =>
      expect(create).toHaveBeenCalledWith(
        expect.objectContaining({ kind: "native", base_url: "" }),
      ),
    );
  });

  it("asks for the address again when the kind changes back from Something else", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([]);
    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByTestId("media-manager-add"));

    fireEvent.change(screen.getByTestId("media-manager-kind"), {
      target: { value: "native" },
    });
    expect(screen.getByTestId("media-manager-save")).toBeEnabled();

    fireEvent.change(screen.getByTestId("media-manager-kind"), {
      target: { value: "radarr" },
    });
    expect(screen.getByTestId("media-manager-save")).toBeDisabled();
    expect(
      screen.queryByText(/^Optional\. Leave it blank/),
    ).not.toBeInTheDocument();
  });

  // #599: what matters is not that a dialog appears, but that nothing is deleted until the
  // dialog is confirmed.
  describe("removing a connection", () => {
    function threeConnections() {
      return [
        connection({ id: 1, name: "Deluno" }),
        connection({ id: 2, kind: "radarr", name: "Radarr" }),
        connection({ id: 3, kind: "sonarr", name: "Sonarr" }),
      ];
    }

    it("does not delete anything on the first click", async () => {
      vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue(
        threeConnections(),
      );
      const del = vi
        .spyOn(api, "deleteMediaManagerConnection")
        .mockResolvedValue(undefined);

      render(<MediaManagersTab />, { wrapper });
      fireEvent.click(
        (await screen.findAllByTestId("media-manager-remove"))[1],
      );

      expect(
        await screen.findByTestId("media-manager-remove-confirm"),
      ).toBeInTheDocument();
      expect(del).not.toHaveBeenCalled();
      // And the connection is still on screen behind the dialog.
      expect(screen.getByText("Radarr")).toBeInTheDocument();
    });

    it("names the connection it is about to remove, not just 'this one'", async () => {
      vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue(
        threeConnections(),
      );
      render(<MediaManagersTab />, { wrapper });

      // Second of three: the case where an unnamed prompt would be useless.
      fireEvent.click(
        (await screen.findAllByTestId("media-manager-remove"))[1],
      );

      const dialog = screen.getByTestId("media-manager-remove-confirm");
      expect(dialog).toHaveTextContent("Remove Radarr?");
      expect(dialog).not.toHaveTextContent("Remove Deluno?");
      expect(dialog).not.toHaveTextContent("Remove Sonarr?");
    });

    it("leaves the connection alone when the dialog is cancelled", async () => {
      vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue(
        threeConnections(),
      );
      const del = vi
        .spyOn(api, "deleteMediaManagerConnection")
        .mockResolvedValue(undefined);

      render(<MediaManagersTab />, { wrapper });
      fireEvent.click(
        (await screen.findAllByTestId("media-manager-remove"))[1],
      );
      fireEvent.click(
        screen.getByTestId("media-manager-remove-confirm-cancel"),
      );

      await waitFor(() =>
        expect(
          screen.queryByTestId("media-manager-remove-confirm"),
        ).not.toBeInTheDocument(),
      );
      expect(del).not.toHaveBeenCalled();
      expect(screen.getByText("Radarr")).toBeInTheDocument();
      expect(screen.getAllByTestId("media-manager-card")).toHaveLength(3);
    });

    it("leaves the connection alone when Escape closes the dialog", async () => {
      vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue(
        threeConnections(),
      );
      const del = vi
        .spyOn(api, "deleteMediaManagerConnection")
        .mockResolvedValue(undefined);

      render(<MediaManagersTab />, { wrapper });
      fireEvent.click(
        (await screen.findAllByTestId("media-manager-remove"))[1],
      );
      fireEvent.keyDown(document, { key: "Escape" });

      await waitFor(() =>
        expect(
          screen.queryByTestId("media-manager-remove-confirm"),
        ).not.toBeInTheDocument(),
      );
      expect(del).not.toHaveBeenCalled();
      expect(screen.getAllByTestId("media-manager-card")).toHaveLength(3);
    });

    it("opens with focus on the safe choice, so Enter keeps the connection", async () => {
      vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue(
        threeConnections(),
      );
      render(<MediaManagersTab />, { wrapper });
      fireEvent.click(
        (await screen.findAllByTestId("media-manager-remove"))[1],
      );

      expect(
        screen.getByTestId("media-manager-remove-confirm-cancel"),
      ).toHaveFocus();
      expect(
        screen.getByTestId("media-manager-remove-confirm-confirm"),
      ).not.toHaveFocus();
    });

    it("deletes only the confirmed connection, once confirmed", async () => {
      vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue(
        threeConnections(),
      );
      const del = vi
        .spyOn(api, "deleteMediaManagerConnection")
        .mockResolvedValue(undefined);

      render(<MediaManagersTab />, { wrapper });
      fireEvent.click(
        (await screen.findAllByTestId("media-manager-remove"))[1],
      );
      fireEvent.click(
        screen.getByTestId("media-manager-remove-confirm-confirm"),
      );

      await waitFor(() => expect(del).toHaveBeenCalledTimes(1));
      expect(del).toHaveBeenCalledWith(2);
    });

    it("keeps the dialog open and says why when the removal fails", async () => {
      vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue(
        threeConnections(),
      );
      vi.spyOn(api, "deleteMediaManagerConnection").mockRejectedValue(
        new Error("Could not reach the server."),
      );

      render(<MediaManagersTab />, { wrapper });
      fireEvent.click(
        (await screen.findAllByTestId("media-manager-remove"))[1],
      );
      fireEvent.click(
        screen.getByTestId("media-manager-remove-confirm-confirm"),
      );

      expect(await screen.findByRole("alert")).toHaveTextContent(
        "Could not reach the server.",
      );
      expect(
        screen.getByTestId("media-manager-remove-confirm"),
      ).toBeInTheDocument();
      expect(screen.getAllByTestId("media-manager-card")).toHaveLength(3);
    });
  });

  it("shows a loading state before the media managers arrive", () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockReturnValue(
      new Promise(() => {}),
    );
    render(<MediaManagersTab />, { wrapper });

    expect(screen.getByText("Loading media managers")).toBeInTheDocument();
  });

  it("shows a plain error instead of the empty state when the load fails", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockRejectedValue(
      new Error("boom"),
    );
    render(<MediaManagersTab />, { wrapper });

    expect(
      await screen.findByTestId("settings-load-error"),
    ).toBeInTheDocument();
    expect(
      screen.queryByText(/Nothing is connected yet/i),
    ).not.toBeInTheDocument();
  });

  it("calls a media manager by that name, never an app", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([]);
    render(<MediaManagersTab />, { wrapper });
    await screen.findByText(/Nothing is connected yet/i);

    expect(screen.getByTestId("media-manager-add")).toHaveTextContent(
      "Add media manager",
    );
    fireEvent.click(screen.getByTestId("media-manager-add"));
    expect(screen.getByText("Which media manager is it?")).toBeInTheDocument();
  });

  it("leaves download clients to their own tab", async () => {
    vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([]);
    render(<MediaManagersTab />, { wrapper });
    await screen.findByText(/Nothing is connected yet/i);

    expect(
      screen.queryByTestId("suite-settings-download-clients"),
    ).not.toBeInTheDocument();
    expect(screen.queryByTestId("download-client-add")).not.toBeInTheDocument();
  });

  describe("after adding a media manager", () => {
    function fillAndSubmit() {
      fireEvent.change(screen.getByTestId("media-manager-base-url"), {
        target: { value: "http://192.0.2.10:5099" },
      });
      fireEvent.click(screen.getByTestId("media-manager-save"));
    }

    it("offers to create its secret right away", async () => {
      vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([]);
      vi.spyOn(api, "createMediaManagerConnection").mockResolvedValue(
        connection({ name: "New One" }),
      );
      const generate = vi
        .spyOn(api, "generateMediaManagerWebhookSecret")
        .mockResolvedValue({
          connection_id: 1,
          webhook_secret: "brand-new-secret",
          webhook_url_path: "/api/v1/intake/webhook/deluno",
          header_name: "X-Webhook-Secret",
        });

      render(<MediaManagersTab />, { wrapper });
      fireEvent.click(await screen.findByTestId("media-manager-add"));
      fillAndSubmit();

      expect(
        await screen.findByText("Create a secret for New One now?"),
      ).toBeInTheDocument();

      fireEvent.click(screen.getByTestId("media-manager-new-secret-create"));

      await waitFor(() => expect(generate).toHaveBeenCalledWith(1));
      expect(
        await screen.findByTestId("media-manager-secret"),
      ).toHaveTextContent("brand-new-secret");
    });

    it("goes away without creating a secret when declined", async () => {
      vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([]);
      vi.spyOn(api, "createMediaManagerConnection").mockResolvedValue(
        connection({ name: "New One" }),
      );

      render(<MediaManagersTab />, { wrapper });
      fireEvent.click(await screen.findByTestId("media-manager-add"));
      fillAndSubmit();

      fireEvent.click(
        await screen.findByTestId("media-manager-new-secret-dismiss"),
      );

      expect(
        screen.queryByTestId("media-manager-new-secret-prompt"),
      ).not.toBeInTheDocument();
    });
  });
});
