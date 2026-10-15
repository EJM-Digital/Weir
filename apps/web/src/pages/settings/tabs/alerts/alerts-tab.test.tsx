import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import * as api from "../../../../lib/settings/settings-api";
import type {
  NotificationChannelListOut,
  NotificationChannelOut,
} from "../../../../lib/settings/types";
import { AlertsTab } from "./alerts-tab";

function channel(over: Partial<NotificationChannelOut> = {}) {
  return {
    id: 1,
    label: "Discord alerts",
    provider: "discord",
    url: "https://example.invalid/••••",
    has_token: true,
    events: ["job_failed"],
    enabled: true,
    created_at: "2026-07-28T07:00:00Z",
    updated_at: "2026-07-28T07:00:00Z",
    ...over,
  } satisfies NotificationChannelOut;
}

function channelList(items: NotificationChannelOut[]) {
  return {
    items,
    supported_events: ["job_failed", "job_completed"],
    supported_providers: ["webhook", "discord"],
  } satisfies NotificationChannelListOut;
}

function threeChannels() {
  return [
    channel({ id: 1, label: "Discord alerts" }),
    channel({ id: 2, label: "Ops webhook", provider: "webhook" }),
    channel({ id: 3, label: "Pager bridge", provider: "webhook" }),
  ];
}

function wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
}

/** Click Remove on one named channel's own row, so the test cannot pick the wrong one. */
async function openRemoveDialogFor(label: string) {
  const row = (await screen.findByText(label)).closest("tr");
  if (!(row instanceof HTMLElement)) throw new Error(`no row for ${label}`);
  const remove = Array.from(row.querySelectorAll("button")).find(
    (b) => b.textContent === "Remove",
  );
  if (!remove) throw new Error(`no Remove button for ${label}`);
  fireEvent.click(remove);
}

afterEach(() => {
  vi.restoreAllMocks();
});

// #599: the point of these tests is not that a dialog shows up, but that nothing is deleted
// until it is confirmed.
describe("SettingsNotificationsTab removal confirmation", () => {
  it("does not delete anything on the first click", async () => {
    vi.spyOn(api, "fetchNotificationChannels").mockResolvedValue(
      channelList(threeChannels()),
    );
    const del = vi
      .spyOn(api, "deleteNotificationChannel")
      .mockResolvedValue(undefined);

    render(<AlertsTab />, { wrapper });
    await openRemoveDialogFor("Ops webhook");

    expect(
      await screen.findByTestId("notification-channel-remove-confirm"),
    ).toBeInTheDocument();
    expect(del).not.toHaveBeenCalled();
    expect(screen.getByText("Ops webhook")).toBeInTheDocument();
  });

  it("names the channel it is about to remove, not just 'this one'", async () => {
    vi.spyOn(api, "fetchNotificationChannels").mockResolvedValue(
      channelList(threeChannels()),
    );
    render(<AlertsTab />, { wrapper });

    // Second of three: the case where an unnamed prompt would be useless.
    await openRemoveDialogFor("Ops webhook");

    const dialog = screen.getByTestId("notification-channel-remove-confirm");
    expect(dialog).toHaveTextContent("Remove Ops webhook?");
    expect(dialog).not.toHaveTextContent("Remove Discord alerts?");
    expect(dialog).not.toHaveTextContent("Remove Pager bridge?");
  });

  it("leaves the channel alone when the dialog is cancelled", async () => {
    vi.spyOn(api, "fetchNotificationChannels").mockResolvedValue(
      channelList(threeChannels()),
    );
    const del = vi
      .spyOn(api, "deleteNotificationChannel")
      .mockResolvedValue(undefined);

    render(<AlertsTab />, { wrapper });
    await openRemoveDialogFor("Ops webhook");
    fireEvent.click(
      screen.getByTestId("notification-channel-remove-confirm-cancel"),
    );

    await waitFor(() =>
      expect(
        screen.queryByTestId("notification-channel-remove-confirm"),
      ).not.toBeInTheDocument(),
    );
    expect(del).not.toHaveBeenCalled();
    expect(screen.getByText("Ops webhook")).toBeInTheDocument();
    expect(screen.getByText("Discord alerts")).toBeInTheDocument();
    expect(screen.getByText("Pager bridge")).toBeInTheDocument();
  });

  it("leaves the channel alone when Escape closes the dialog", async () => {
    vi.spyOn(api, "fetchNotificationChannels").mockResolvedValue(
      channelList(threeChannels()),
    );
    const del = vi
      .spyOn(api, "deleteNotificationChannel")
      .mockResolvedValue(undefined);

    render(<AlertsTab />, { wrapper });
    await openRemoveDialogFor("Ops webhook");
    fireEvent.keyDown(document, { key: "Escape" });

    await waitFor(() =>
      expect(
        screen.queryByTestId("notification-channel-remove-confirm"),
      ).not.toBeInTheDocument(),
    );
    expect(del).not.toHaveBeenCalled();
    expect(screen.getByText("Ops webhook")).toBeInTheDocument();
  });

  it("opens with focus on the safe choice, so Enter keeps the channel", async () => {
    vi.spyOn(api, "fetchNotificationChannels").mockResolvedValue(
      channelList(threeChannels()),
    );
    render(<AlertsTab />, { wrapper });
    await openRemoveDialogFor("Ops webhook");

    expect(
      screen.getByTestId("notification-channel-remove-confirm-cancel"),
    ).toHaveFocus();
    expect(
      screen.getByTestId("notification-channel-remove-confirm-confirm"),
    ).not.toHaveFocus();
  });

  it("deletes only the confirmed channel, once confirmed", async () => {
    vi.spyOn(api, "fetchNotificationChannels").mockResolvedValue(
      channelList(threeChannels()),
    );
    const del = vi
      .spyOn(api, "deleteNotificationChannel")
      .mockResolvedValue(undefined);

    render(<AlertsTab />, { wrapper });
    await openRemoveDialogFor("Ops webhook");
    fireEvent.click(
      screen.getByTestId("notification-channel-remove-confirm-confirm"),
    );

    await waitFor(() => expect(del).toHaveBeenCalledTimes(1));
    expect(del).toHaveBeenCalledWith(2);
  });

  it("keeps the dialog open and says why when the removal fails", async () => {
    vi.spyOn(api, "fetchNotificationChannels").mockResolvedValue(
      channelList(threeChannels()),
    );
    vi.spyOn(api, "deleteNotificationChannel").mockRejectedValue(
      new Error("Could not reach the server."),
    );

    render(<AlertsTab />, { wrapper });
    await openRemoveDialogFor("Ops webhook");
    fireEvent.click(
      screen.getByTestId("notification-channel-remove-confirm-confirm"),
    );

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Could not reach the server.",
    );
    expect(
      screen.getByTestId("notification-channel-remove-confirm"),
    ).toBeInTheDocument();
    expect(screen.getByText("Ops webhook")).toBeInTheDocument();
  });
});

describe("AlertsTab adding an alert", () => {
  it("says what alerts do while there are none, and offers the first one", async () => {
    vi.spyOn(api, "fetchNotificationChannels").mockResolvedValue(
      channelList([]),
    );

    render(<AlertsTab />, { wrapper });

    const empty = await screen.findByTestId("alerts-empty");
    expect(empty).toHaveTextContent("posts to Discord");
    fireEvent.click(
      screen.getByRole("button", { name: "Add your first alert" }),
    );
    expect(
      screen.getByRole("dialog", { name: "New alert" }),
    ).toBeInTheDocument();
  });

  it("opens the form in a drawer with focus on its label, and closes it on Cancel", async () => {
    vi.spyOn(api, "fetchNotificationChannels").mockResolvedValue(
      channelList(threeChannels()),
    );

    render(<AlertsTab />, { wrapper });
    fireEvent.click(await screen.findByRole("button", { name: "Add alert" }));

    expect(screen.getByLabelText("Label")).toHaveFocus();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add alert" })).toBeEnabled();
  });
});

describe("AlertsTab loading and errors", () => {
  it("shows a loading state before alerts arrive", () => {
    vi.spyOn(api, "fetchNotificationChannels").mockReturnValue(
      new Promise(() => {}),
    );

    render(<AlertsTab />, { wrapper });

    expect(screen.getByText("Loading alerts")).toBeInTheDocument();
  });

  it("says alerts could not be loaded instead of showing none", async () => {
    vi.spyOn(api, "fetchNotificationChannels").mockRejectedValue(
      new Error("boom"),
    );

    render(<AlertsTab />, { wrapper });

    expect(await screen.findByTestId("settings-load-error")).toHaveTextContent(
      "Weir couldn’t load your alerts. Reload the page to try again.",
    );
    expect(screen.queryByTestId("alerts-empty")).not.toBeInTheDocument();
  });
});
