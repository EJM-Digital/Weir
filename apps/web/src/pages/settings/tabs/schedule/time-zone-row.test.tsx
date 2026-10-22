import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import * as settingsApi from "../../../../lib/settings/settings-api";
import type { AppSettings } from "../../../../lib/settings/types";
import { savedZone, TimeZoneRow } from "./time-zone-row";

afterEach(() => {
  vi.restoreAllMocks();
});

function settingsIn(zone: string): AppSettings {
  return {
    setup_wizard_state: "completed",
    app_timezone: zone,
    log_retention_days: 30,
    activity_retention_days: 0,
    configuration_backup_enabled: false,
    configuration_backup_interval_hours: 24,
    configuration_backup_preferred_time: "02:00",
    configuration_backup_last_run_at: null,
    updated_at: "2026-04-23T00:00:00Z",
  };
}

function renderRow(zone: string) {
  const put = vi
    .spyOn(settingsApi, "putAppSettings")
    .mockImplementation(async (body) => ({
      ...settingsIn(zone),
      app_timezone: body.app_timezone,
    }));
  render(
    <QueryClientProvider client={new QueryClient()}>
      <span id="zone-label">Time zone</span>
      <TimeZoneRow
        labelId="zone-label"
        settings={settingsIn(zone)}
        editable
        now={new Date("2026-10-15T12:00:00Z")}
      />
    </QueryClientProvider>,
  );
  return put;
}

const picker = () => screen.getByRole("button", { name: "Time zone" });

describe("the time zone a server is in", () => {
  it("shows UTC as chosen on a server in UTC", () => {
    renderRow("UTC");

    expect(picker()).toHaveTextContent(/^UTC — Coordinated Universal Time/);
    expect(screen.queryByTestId("schedule-save-timezone")).toBeNull();
  });

  it("shows a listed zone as chosen", () => {
    renderRow("Europe/Berlin");

    expect(picker()).toHaveTextContent(/Germany — Berlin/);
  });

  it("shows a valid zone the list lacks as chosen, with nothing to save", () => {
    renderRow("Europe/Dublin");

    expect(picker()).toHaveTextContent(/^Europe\/Dublin \(UTC\+[01]\)$/);
    expect(screen.queryByTestId("schedule-save-timezone")).toBeNull();
  });

  it("offers that zone beside the listed ones, and saves another that is picked", async () => {
    const put = renderRow("Europe/Dublin");

    fireEvent.click(picker());
    expect(
      screen.getByRole("option", { name: /^Europe\/Dublin/ }),
    ).toHaveAttribute("aria-selected", "true");
    fireEvent.click(screen.getByRole("option", { name: /Japan — Tokyo/ }));
    fireEvent.click(screen.getByTestId("schedule-save-timezone"));

    await waitFor(() => expect(put).toHaveBeenCalledTimes(1));
    expect(put.mock.calls[0]?.[0]).toMatchObject({
      app_timezone: "Asia/Tokyo",
    });
  });

  it("reads a saved zone as the zone itself, and a missing one as UTC", () => {
    expect(savedZone(settingsIn("Europe/Dublin"))).toBe("Europe/Dublin");
    expect(savedZone(settingsIn(" Europe/Dublin "))).toBe("Europe/Dublin");
    expect(savedZone(settingsIn(""))).toBe("UTC");
  });
});

describe("saving a new time zone", () => {
  it("says Saving, and offers no second save, until the server answers", async () => {
    const put = renderRow("Europe/Dublin");
    put.mockReturnValue(new Promise(() => undefined));

    fireEvent.click(picker());
    fireEvent.click(screen.getByRole("option", { name: /Japan — Tokyo/ }));
    fireEvent.click(screen.getByTestId("schedule-save-timezone"));

    expect(await screen.findByText("Saving…")).toBeInTheDocument();
    expect(screen.getByTestId("schedule-save-timezone")).toBeDisabled();
  });
});
