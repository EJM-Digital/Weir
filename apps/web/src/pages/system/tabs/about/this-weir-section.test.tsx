import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AppSettings } from "../../../../lib/settings/types";
import type { SystemOverview } from "../../../../lib/system/system-stats-types";
import { testOverview } from "../../../processing/dashboard/system/test-stats";
import { ThisWeirSection } from "./this-weir-section";

const mocks = vi.hoisted(() => ({ overview: vi.fn(), updateState: vi.fn() }));

vi.mock("../../../../lib/system/use-system-stats", () => ({
  useSystemOverviewQuery: () => mocks.overview(),
}));
vi.mock("../../../../lib/system/readiness-queries", () => ({
  useSystemReadinessQuery: () => ({ data: undefined }),
}));
vi.mock("../../../../lib/settings/queries", () => ({
  useUpdateStatusQuery: () => ({ data: undefined }),
  useUpdateStateQuery: () => mocks.updateState(),
}));
vi.mock("../../../../lib/ui/mm-format-date", () => ({
  useAppDateFormatter: () => (iso: string | null | undefined) =>
    `10 Oct 2026 08:44 (${iso ?? ""})`,
}));

const settings = { setup_wizard_state: "completed" } as AppSettings;

const backup = {
  path: "C:\\ProgramData\\Weir\\backups\\pre-update\\weir-0076-to-1.0.0-rc.13-20261010T084400Z.db",
  taken_at: "2026-10-10T08:44:00Z",
  from_version: "1.0.0-rc.12",
  to_version: "1.0.0-rc.13",
  in_data_folder: true,
};

function show(
  overview: Partial<SystemOverview>,
  updateState: { not_updated_reason?: string } = {},
) {
  mocks.overview.mockReturnValue({ data: { ...testOverview, ...overview } });
  mocks.updateState.mockReturnValue({
    data: { downloaded: true, pending_version: "1.0.0-rc.13", ...updateState },
  });
  render(
    <MemoryRouter>
      <ThisWeirSection settings={settings} />
    </MemoryRouter>,
  );
}

describe("System › About: the copy taken before an update", () => {
  beforeEach(() => {
    mocks.overview.mockReset();
    mocks.updateState.mockReset();
  });

  it("says when Weir saved a copy of its data, between which versions, and where", () => {
    show({ last_update_backup: backup });

    expect(screen.getByTestId("about-update-backup")).toHaveTextContent(
      `Before updating from 1.0.0-rc.12 to 1.0.0-rc.13 on 10 Oct 2026 08:44 (2026-10-10T08:44:00Z), Weir saved a copy of its data at ${backup.path}.`,
    );
  });

  it("leaves out the old version when the server that took the copy did not know it", () => {
    show({ last_update_backup: { ...backup, from_version: null } });

    expect(screen.getByTestId("about-update-backup")).toHaveTextContent(
      "Before updating to 1.0.0-rc.13 on",
    );
  });

  it("says in Docker that the copy is inside the WEIR_HOME volume", () => {
    show({ runs_as: "docker", last_update_backup: backup });

    expect(screen.getByTestId("about-update-backup")).toHaveTextContent(
      `at ${backup.path}, inside the WEIR_HOME volume.`,
    );
  });

  it("does not claim the volume for a copy kept outside the data folder", () => {
    show({
      runs_as: "docker",
      last_update_backup: { ...backup, in_data_folder: false },
    });

    expect(screen.getByTestId("about-update-backup")).not.toHaveTextContent(
      "WEIR_HOME",
    );
  });

  it("says nothing about a copy when no update has made one", () => {
    show({ last_update_backup: null });

    expect(screen.queryByTestId("about-update-backup")).not.toBeInTheDocument();
    expect(screen.queryByTestId("about-not-updated")).not.toBeInTheDocument();
  });

  it("says why Weir did not update when it could not save a copy first", () => {
    show(
      { last_update_backup: null },
      {
        not_updated_reason:
          "Drive C: needs about 480 MB free to hold the copy and has 120 MB. Free up space there, then try again.",
      },
    );

    const note = screen.getByTestId("about-not-updated");
    expect(note).toHaveAttribute("data-status", "attention");
    expect(note).toHaveTextContent(
      "Weir didn't update because it couldn't save a copy of its data: Drive C: needs about 480 MB free",
    );
  });
});

describe("System › About: running the setup wizard again", () => {
  beforeEach(() => {
    mocks.overview.mockReset();
    mocks.updateState.mockReset();
    mocks.overview.mockReturnValue({ data: testOverview });
    mocks.updateState.mockReturnValue({ data: undefined });
  });

  function showWith(setupWizardState: string) {
    render(
      <MemoryRouter initialEntries={["/system?tab=about"]}>
        <Routes>
          <Route
            path="/system"
            element={
              <ThisWeirSection
                settings={
                  { setup_wizard_state: setupWizardState } as AppSettings
                }
              />
            }
          />
          <Route path="/setup-wizard" element={<p>The setup wizard</p>} />
        </Routes>
      </MemoryRouter>,
    );
    return screen.getByTestId("suite-settings-open-setup-wizard");
  }

  it("opens the wizard from the button", () => {
    fireEvent.click(showWith("completed"));

    expect(screen.getByText("The setup wizard")).toBeInTheDocument();
  });

  it.each([
    ["completed", "You finished the wizard."],
    ["skipped", "You skipped the wizard."],
    ["pending", "The wizard has not been finished yet."],
    ["", "The wizard has not been finished yet."],
  ])("when the wizard is %j, the button says %j", (state, note) => {
    expect(showWith(state)).toHaveAttribute("title", note);
  });
});
