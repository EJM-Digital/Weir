import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type {
  UpdateStateOut,
  UpdateStatus,
} from "../../../../lib/settings/types";
import { UpdateSection } from "./update-section";

const mocks = vi.hoisted(() => ({
  useUpdateStatusQuery: vi.fn(),
  useUpdateStateQuery: vi.fn(),
}));

vi.mock("../../../../lib/settings/queries", () => ({
  useUpdateStatusQuery: () => mocks.useUpdateStatusQuery(),
  useUpdateStateQuery: () => mocks.useUpdateStateQuery(),
  useAppSettingsQuery: () => ({ data: { app_timezone: "UTC" } }),
}));

vi.mock("./update-actions", () => ({
  UpdateActions: () => <div data-testid="update-actions" />,
}));
vi.mock("./update-preferences", () => ({ UpdatePreferences: () => null }));

const LIMITED =
  "GitHub is limiting update checks from your network right now. Weir will check again at 11:35 pm.";

function status(
  limited: Pick<UpdateStatus, "summary"> &
    Pick<
      UpdateStatus,
      | "latest_version"
      | "published_at"
      | "retry_at"
      | "known_update_available"
      | "docker_update_command"
      | "windows_installer_url"
      | "release_url"
    >,
  installType = "source",
): UpdateStatus {
  return {
    current_version: "3.2.16",
    install_type: installType,
    in_app_upgrade_supported: false,
    status: "rate_limited",
    ...limited,
  };
}

function showStatus(update: UpdateStatus) {
  mocks.useUpdateStatusQuery.mockReturnValue({
    data: update,
    isPending: false,
    isFetching: false,
    refetch: vi.fn(),
  });
  render(<UpdateSection />);
}

describe("UpdateSection while GitHub limits update checks", () => {
  beforeEach(() => {
    mocks.useUpdateStatusQuery.mockReset();
    mocks.useUpdateStateQuery.mockReset();
  });

  it("says plainly when Weir will check again", () => {
    showStatus(
      status({
        summary: LIMITED,
        retry_at: "2026-01-15T23:35:00Z",
      }),
    );

    const line = screen.getByTestId("suite-settings-release-status");
    expect(line).toHaveTextContent("Limited by GitHub");
    expect(line).toHaveTextContent(LIMITED);
    expect(
      screen.queryByTestId("suite-settings-last-known-release"),
    ).toBeNull();
  });

  it("keeps the last release it knew of in view", () => {
    showStatus(
      status({
        summary: LIMITED,
        latest_version: "3.3.0",
        published_at: "2026-10-01T10:00:00Z",
      }),
    );

    expect(
      screen.getByTestId("suite-settings-last-known-release"),
    ).toHaveTextContent(/Latest known: 3\.3\.0, published .*2026/);
  });

  it("still offers the update Weir already knew of: the Docker command", () => {
    showStatus(
      status(
        {
          summary: LIMITED,
          latest_version: "3.3.0",
          known_update_available: true,
          docker_update_command: "docker compose pull && docker compose up -d",
        },
        "docker",
      ),
    );

    expect(
      screen.getByText("docker compose pull && docker compose up -d"),
    ).toBeInTheDocument();
  });

  it("keeps the installer as a small link on Windows, where the update buttons take the update", () => {
    showStatus(
      status(
        {
          summary: LIMITED,
          latest_version: "3.3.0",
          known_update_available: true,
          windows_installer_url: "https://example.test/Weir-win-Setup.exe",
        },
        "windows",
      ),
    );

    expect(
      screen.getByRole("link", { name: "Download installer →" }),
    ).toHaveAttribute("href", "https://example.test/Weir-win-Setup.exe");
    expect(screen.getByTestId("update-actions")).toBeInTheDocument();
    expect(
      screen.queryByRole("link", { name: "Download the update" }),
    ).toBeNull();
    expect(screen.queryByRole("button", { name: "Check again →" })).toBeNull();
  });

  it("offers the update buttons on Windows only", () => {
    showStatus(status({ summary: LIMITED }, "docker"));

    expect(screen.queryByTestId("update-actions")).toBeNull();
  });

  it("offers no update when the last release it knew of is not newer", () => {
    showStatus(
      status(
        {
          summary: LIMITED,
          latest_version: "3.2.16",
          known_update_available: false,
          docker_update_command: "docker compose pull && docker compose up -d",
        },
        "docker",
      ),
    );

    expect(screen.queryByText(/docker compose pull/)).toBeNull();
  });

  it("still offers Check again, which the server answers without asking GitHub", () => {
    showStatus(status({ summary: LIMITED }));

    expect(screen.getByRole("button", { name: "Check again →" })).toBeEnabled();
  });

  describe("when an update is available on Windows", () => {
    function showAvailable() {
      showStatus({
        ...status(
          {
            summary: "Weir 9.9.9 is available.",
            latest_version: "9.9.9",
            windows_installer_url: "https://example.test/Weir-win-Setup.exe",
            release_url: "https://example.test/release",
          },
          "windows",
        ),
        status: "update_available",
      });
    }

    it("says it is available, not ready, and says which version in a sentence", () => {
      showAvailable();

      const line = screen.getByTestId("suite-settings-release-status");
      expect(line).toHaveTextContent("Update available");
      expect(line).not.toHaveTextContent("Update ready");
      expect(line).toHaveTextContent(
        "Weir v9.9.9 is available (you have v3.2.16)",
      );
    });

    it("keeps the installer and the release notes as quiet links under the buttons, not controls in the header", () => {
      showAvailable();

      for (const name of ["Download installer →", "Release notes →"]) {
        const link = screen.getByRole("link", { name });
        expect(link).toHaveClass("mm-quiet-link");
        expect(link.closest(".mm-panel__aside")).toBeNull();
      }
    });
  });

  it("says which version is running when Weir is up to date", () => {
    showStatus({ ...status({ summary: "" }, "windows"), status: "up_to_date" });

    expect(
      screen.getByTestId("suite-settings-release-status"),
    ).toHaveTextContent("You have Weir v3.2.16");
  });
});

describe("UpdateSection while update checking is off", () => {
  const OFF_SUMMARY =
    "Update checking is off, so Weir is not looking for new versions. Press Check now to look once, or turn it on below.";

  beforeEach(() => {
    mocks.useUpdateStatusQuery.mockReset();
    mocks.useUpdateStateQuery.mockReset();
  });

  function showOff(
    state: Partial<UpdateStateOut> | undefined,
    installType = "windows",
  ) {
    mocks.useUpdateStateQuery.mockReturnValue({
      data: state && {
        downloaded: false,
        pending_version: null,
        state: "idle",
        failure: null,
        tray_running: true,
        ...state,
      },
    });
    showStatus({
      ...status({ summary: OFF_SUMMARY }, installType),
      status: "off",
    });
  }

  it("says checking is off and how to look once, before anything has been checked", () => {
    showOff({});

    const line = screen.getByTestId("suite-settings-release-status");
    expect(line).toHaveTextContent("Off");
    expect(line).toHaveTextContent(OFF_SUMMARY);
    expect(screen.queryByTestId("suite-settings-manual-check")).toBeNull();
  });

  it("says the same while the tray has not answered yet", () => {
    showOff(undefined);

    expect(
      screen.getByTestId("suite-settings-release-status"),
    ).toHaveTextContent(OFF_SUMMARY);
  });

  it("answers a check made by hand that found nothing newer, and when it was made", () => {
    showOff({ last_checked_at: "2026-10-21T09:30:00Z" });

    const line = screen.getByTestId("suite-settings-release-status");
    expect(line).toHaveTextContent("Up to date");
    expect(line).toHaveTextContent("You have Weir v3.2.16");
    expect(line).not.toHaveTextContent(OFF_SUMMARY);
    const note = screen.getByTestId("suite-settings-manual-check");
    expect(note).toHaveTextContent(/Checked .*2026, 9:30/);
    expect(note).toHaveTextContent(
      "Update checking is off, so Weir will not look again by itself.",
    );
  });

  it("names the newer version a check made by hand found", () => {
    showOff({
      last_checked_at: "2026-10-21T09:30:00Z",
      pending_version: "9.9.9",
    });

    const line = screen.getByTestId("suite-settings-release-status");
    expect(line).toHaveTextContent("Update available");
    expect(line).toHaveTextContent(
      "Weir v9.9.9 is available (you have v3.2.16)",
    );
    expect(screen.getByTestId("suite-settings-manual-check")).toHaveTextContent(
      "Update checking is off",
    );
  });

  it("keeps the last answer in view while the next check is under way", () => {
    showOff({ last_checked_at: "2026-10-21T09:30:00Z", state: "checking" });

    expect(
      screen.getByTestId("suite-settings-release-status"),
    ).toHaveTextContent("You have Weir v3.2.16");
  });

  it("leaves a Docker install alone, which has no tray to hold the choice", () => {
    showOff({ last_checked_at: "2026-10-21T09:30:00Z" }, "docker");

    expect(screen.queryByTestId("suite-settings-manual-check")).toBeNull();
  });
});
