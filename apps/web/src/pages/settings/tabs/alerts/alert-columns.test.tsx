import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { signedInAs } from "../../../../test/signed-in-as";
import * as api from "../../../../lib/settings/settings-api";
import type { NotificationChannelOut } from "../../../../lib/settings/types";
import { AlertsTab } from "./alerts-tab";

function channel(
  id: number,
  label: string,
  events: string[],
): NotificationChannelOut {
  return {
    id,
    label,
    provider: "webhook",
    url: "https://example.invalid/••••",
    has_token: true,
    events,
    enabled: true,
    created_at: "2026-07-28T07:00:00Z",
    updated_at: "2026-07-28T07:00:00Z",
  };
}

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

async function showAlerts() {
  vi.spyOn(api, "fetchNotificationChannels").mockResolvedValue({
    items: [
      channel(1, "Pager", ["job_failed"]),
      channel(2, "Discord", ["job_failed", "job_completed"]),
      channel(3, "Ops", ["job_failed"]),
    ],
    supported_events: ["job_failed", "job_completed"],
    supported_providers: ["webhook"],
  });
  render(<AlertsTab />, { wrapper });
  await screen.findByText("Pager");
}

function alertNames() {
  return screen
    .getAllByRole("row")
    .slice(1)
    .map((row) => row.querySelector("th")?.querySelector("span")?.textContent);
}

beforeEach(() => {
  localStorage.clear();
  signedInAs("admin");
});
afterEach(() => vi.restoreAllMocks());

describe("the alerts table", () => {
  it("lists the alerts as they come until a heading sorts them", async () => {
    await showAlerts();

    expect(alertNames()).toEqual(["Pager", "Discord", "Ops"]);
  });

  it("sorts by name, and reverses on the next click", async () => {
    await showAlerts();

    fireEvent.click(screen.getByRole("button", { name: "Alert" }));
    expect(alertNames()).toEqual(["Discord", "Ops", "Pager"]);

    fireEvent.click(screen.getByRole("button", { name: "Alert" }));
    expect(alertNames()).toEqual(["Pager", "Ops", "Discord"]);
  });

  it("puts the alerts that hear of an event first when its column is sorted", async () => {
    await showAlerts();

    fireEvent.click(screen.getByRole("button", { name: "Anything finished" }));

    expect(alertNames()[0]).toBe("Discord");
  });

  it("puts the order back with Reset columns", async () => {
    await showAlerts();
    fireEvent.click(screen.getByRole("button", { name: "Alert" }));

    fireEvent.click(screen.getByRole("button", { name: "Columns" }));
    fireEvent.click(screen.getByRole("menuitem", { name: "Reset columns" }));

    expect(alertNames()).toEqual(["Pager", "Discord", "Ops"]);
  });
});
