import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import * as api from "../../../../lib/media-managers/media-managers-api";
import type { MediaManagerConnection } from "../../../../lib/media-managers/media-managers-api";
import { ConnectionEditForm } from "./connection-edit-form";

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
  return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe("ConnectionEditForm", () => {
  it("has no field for a name: the connection is named after its address", () => {
    render(<ConnectionEditForm connection={connection()} onClose={vi.fn()} />, {
      wrapper,
    });

    expect(
      screen.queryByTestId("media-manager-edit-name"),
    ).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Name")).not.toBeInTheDocument();
    expect(screen.getByLabelText("Address")).toBeInTheDocument();
  });

  it("saves the address without touching the api key when it is left blank", async () => {
    const update = vi
      .spyOn(api, "updateMediaManagerConnection")
      .mockResolvedValue(connection({ name: "Deluno on 192.0.2.11" }));
    const onClose = vi.fn();

    render(<ConnectionEditForm connection={connection()} onClose={onClose} />, {
      wrapper,
    });

    fireEvent.change(screen.getByTestId("media-manager-edit-base-url"), {
      target: { value: "http://192.0.2.11:5099" },
    });
    fireEvent.click(screen.getByTestId("media-manager-edit-save"));

    await waitFor(() => expect(update).toHaveBeenCalledTimes(1));
    expect(update).toHaveBeenCalledWith(1, {
      base_url: "http://192.0.2.11:5099",
    });
    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
  });

  it("sends the typed api key only when something was typed", async () => {
    const update = vi
      .spyOn(api, "updateMediaManagerConnection")
      .mockResolvedValue(connection());

    render(<ConnectionEditForm connection={connection()} onClose={vi.fn()} />, {
      wrapper,
    });

    fireEvent.change(screen.getByTestId("media-manager-edit-api-key"), {
      target: { value: "new-key" },
    });
    fireEvent.click(screen.getByTestId("media-manager-edit-save"));

    await waitFor(() => expect(update).toHaveBeenCalledTimes(1));
    expect(update).toHaveBeenCalledWith(1, {
      base_url: "http://192.0.2.10:5099",
      api_key: "new-key",
    });
  });

  it("shows why the save failed instead of closing the form", async () => {
    vi.spyOn(api, "updateMediaManagerConnection").mockRejectedValue(
      new Error("Could not reach the server."),
    );
    const onClose = vi.fn();

    render(<ConnectionEditForm connection={connection()} onClose={onClose} />, {
      wrapper,
    });

    fireEvent.change(screen.getByTestId("media-manager-edit-base-url"), {
      target: { value: "http://192.0.2.11:5099" },
    });
    fireEvent.click(screen.getByTestId("media-manager-edit-save"));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Could not reach the server.",
    );
    expect(onClose).not.toHaveBeenCalled();
  });

  it.each(["radarr", "sonarr", "deluno"] as const)(
    "will not save a %s connection with the address cleared",
    (kind) => {
      render(
        <ConnectionEditForm
          connection={connection({ kind })}
          onClose={vi.fn()}
        />,
        { wrapper },
      );

      fireEvent.change(screen.getByTestId("media-manager-edit-base-url"), {
        target: { value: "  " },
      });

      expect(screen.getByTestId("media-manager-edit-save")).toBeDisabled();
    },
  );

  it("saves a Something else connection with no address, and says the address is optional", async () => {
    const update = vi
      .spyOn(api, "updateMediaManagerConnection")
      .mockResolvedValue(connection({ kind: "native", base_url: "" }));
    const onClose = vi.fn();
    render(
      <ConnectionEditForm
        connection={connection({
          kind: "native",
          name: "Media manager",
          base_url: "",
        })}
        onClose={onClose}
      />,
      { wrapper },
    );

    expect(screen.getByText(/^Optional\. Leave it blank/)).toBeInTheDocument();
    fireEvent.change(screen.getByTestId("media-manager-edit-api-key"), {
      target: { value: "new-key" },
    });
    expect(screen.getByTestId("media-manager-edit-save")).toBeEnabled();
    fireEvent.click(screen.getByTestId("media-manager-edit-save"));

    await waitFor(() => expect(update).toHaveBeenCalledTimes(1));
    expect(update).toHaveBeenCalledWith(1, {
      base_url: "",
      api_key: "new-key",
    });
    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
  });

  it("closes at once when Cancel is pressed with nothing changed", () => {
    const onClose = vi.fn();
    render(<ConnectionEditForm connection={connection()} onClose={onClose} />, {
      wrapper,
    });

    fireEvent.click(screen.getByTestId("media-manager-edit-cancel"));

    expect(onClose).toHaveBeenCalledTimes(1);
    expect(
      screen.queryByTestId("settings-unsaved-changes"),
    ).not.toBeInTheDocument();
  });

  it("shows the downloaded-scan toggle only for Sonarr and Radarr, never Deluno", () => {
    const { rerender } = render(
      <ConnectionEditForm
        connection={connection({ kind: "deluno" })}
        onClose={vi.fn()}
      />,
      { wrapper },
    );
    expect(
      screen.queryByTestId("media-manager-edit-downloaded-scan"),
    ).not.toBeInTheDocument();

    rerender(
      <ConnectionEditForm
        connection={connection({ kind: "sonarr", name: "Sonarr" })}
        onClose={vi.fn()}
      />,
    );
    expect(
      screen.getByTestId("media-manager-edit-downloaded-scan"),
    ).toBeInTheDocument();

    rerender(
      <ConnectionEditForm
        connection={connection({ kind: "radarr", name: "Radarr" })}
        onClose={vi.fn()}
      />,
    );
    expect(
      screen.getByTestId("media-manager-edit-downloaded-scan"),
    ).toBeInTheDocument();
  });

  it("sends downloaded_scan_enabled when saving a Sonarr connection, but never for Deluno", async () => {
    const update = vi
      .spyOn(api, "updateMediaManagerConnection")
      .mockResolvedValue(connection({ kind: "sonarr", name: "Sonarr" }));

    render(
      <ConnectionEditForm
        connection={connection({
          kind: "sonarr",
          name: "Sonarr",
          downloaded_scan_enabled: false,
        })}
        onClose={vi.fn()}
      />,
      { wrapper },
    );

    fireEvent.click(screen.getByTestId("media-manager-edit-downloaded-scan"));
    fireEvent.click(screen.getByTestId("media-manager-edit-save"));

    await waitFor(() => expect(update).toHaveBeenCalledTimes(1));
    expect(update).toHaveBeenCalledWith(1, {
      base_url: "http://192.0.2.10:5099",
      downloaded_scan_enabled: true,
    });
  });

  it("never sends downloaded_scan_enabled when editing a Deluno connection", async () => {
    const update = vi
      .spyOn(api, "updateMediaManagerConnection")
      .mockResolvedValue(connection());

    render(<ConnectionEditForm connection={connection()} onClose={vi.fn()} />, {
      wrapper,
    });

    fireEvent.change(screen.getByTestId("media-manager-edit-base-url"), {
      target: { value: "http://192.0.2.11:5099" },
    });
    fireEvent.click(screen.getByTestId("media-manager-edit-save"));

    await waitFor(() => expect(update).toHaveBeenCalledTimes(1));
    expect(update).toHaveBeenCalledWith(1, {
      base_url: "http://192.0.2.11:5099",
    });
  });

  it("asks before dropping an edited field", async () => {
    const onClose = vi.fn();
    render(<ConnectionEditForm connection={connection()} onClose={onClose} />, {
      wrapper,
    });

    fireEvent.change(screen.getByTestId("media-manager-edit-base-url"), {
      target: { value: "http://192.0.2.11:5099" },
    });
    fireEvent.click(screen.getByTestId("media-manager-edit-cancel"));

    const dialog = await screen.findByTestId("settings-unsaved-changes");
    expect(dialog).toHaveTextContent("Deluno");
    expect(onClose).not.toHaveBeenCalled();

    fireEvent.click(screen.getByTestId("settings-unsaved-changes-confirm"));
    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
  });
});
