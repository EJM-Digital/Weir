import { describe, expect, it } from "vitest";

import type {
  UpdateStateOut,
  UpdateStatus,
} from "../../../../lib/settings/types";
import { updateButtons } from "./update-steps";

function state(overrides: Partial<UpdateStateOut> = {}): UpdateStateOut {
  return {
    downloaded: false,
    pending_version: null,
    state: "idle",
    failure: null,
    tray_running: true,
    ...overrides,
  };
}

function status(overrides: Partial<UpdateStatus> = {}): UpdateStatus {
  return {
    current_version: "1.0.0",
    install_type: "windows",
    in_app_upgrade_supported: true,
    status: "up_to_date",
    summary: "",
    ...overrides,
  };
}

const idle = { asking: false, restarting: false };

function enabled(buttons: ReturnType<typeof updateButtons>) {
  return [
    buttons.check.enabled,
    buttons.download.enabled,
    buttons.apply.enabled,
  ];
}

describe("updateButtons", () => {
  it("offers Check now while update checking is off, since a person asking is never refused", () => {
    const buttons = updateButtons(state(), status({ status: "off" }), idle);

    expect(enabled(buttons)).toEqual([true, false, false]);
  });

  it("offers the download of an update the tray found when a person checked while update checking was off", () => {
    const buttons = updateButtons(
      state({ pending_version: "1.1.0" }),
      status({ status: "off" }),
      idle,
    );

    expect(enabled(buttons)).toEqual([true, true, false]);
    expect(buttons.download.primary).toBe(true);
  });

  it("still offers only the restart for an update downloaded before checking was switched off", () => {
    const buttons = updateButtons(
      state({
        downloaded: true,
        pending_version: "1.1.0",
        state: "downloaded",
      }),
      status({ status: "off" }),
      idle,
    );

    expect(enabled(buttons)).toEqual([false, false, true]);
  });

  it("offers only Check now when Weir is up to date", () => {
    const buttons = updateButtons(state(), status(), idle);

    expect(enabled(buttons)).toEqual([true, false, false]);
    expect(buttons.check.label).toBe("Check now");
    expect(buttons.download.label).toBe("Download update");
    expect(buttons.apply.label).toBe("Restart and apply");
  });

  it("offers the download as well once the tray has found an update", () => {
    const buttons = updateButtons(
      state({ pending_version: "1.1.0" }),
      status(),
      idle,
    );

    expect(enabled(buttons)).toEqual([true, true, false]);
  });

  it("offers the download for an update Weir's own look found, before the tray has looked", () => {
    const found = updateButtons(
      state(),
      status({ status: "update_available" }),
      idle,
    );
    const known = updateButtons(
      state(),
      status({ status: "rate_limited", known_update_available: true }),
      idle,
    );

    expect(enabled(found)).toEqual([true, true, false]);
    expect(enabled(known)).toEqual([true, true, false]);
  });

  it("offers nothing but its own progress while the tray checks", () => {
    const buttons = updateButtons(
      state({ state: "checking" }),
      status({ status: "update_available" }),
      idle,
    );

    expect(enabled(buttons)).toEqual([false, false, false]);
    expect(buttons.check.label).toBe("Checking…");
  });

  it("offers nothing but its own progress while the tray downloads", () => {
    const buttons = updateButtons(
      state({ state: "downloading", pending_version: "1.1.0" }),
      status({ status: "update_available" }),
      idle,
    );

    expect(enabled(buttons)).toEqual([false, false, false]);
    expect(buttons.download.label).toBe("Downloading update…");
  });

  it("offers only the restart once the update is downloaded", () => {
    const buttons = updateButtons(
      state({
        state: "downloaded",
        downloaded: true,
        pending_version: "1.1.0",
      }),
      status({ status: "update_available" }),
      idle,
    );

    expect(enabled(buttons)).toEqual([false, false, true]);
  });

  it("goes back to offering the steps after a failure", () => {
    const buttons = updateButtons(
      state({
        state: "failed",
        pending_version: "1.1.0",
        failure: "Weir could not download the update.",
      }),
      status(),
      idle,
    );

    expect(enabled(buttons)).toEqual([true, true, false]);
  });

  it("waits for an answer the page has asked for", () => {
    const buttons = updateButtons(
      state({ pending_version: "1.1.0" }),
      status(),
      { asking: true, restarting: false },
    );

    expect(enabled(buttons)).toEqual([false, false, false]);
  });

  it("offers nothing more once the restart has been asked for", () => {
    const buttons = updateButtons(
      state({ state: "downloaded", downloaded: true }),
      status(),
      { asking: false, restarting: true },
    );

    expect(enabled(buttons)).toEqual([false, false, false]);
    expect(buttons.apply.label).toBe("Restarting…");
  });

  it("offers nothing until the tray's state is known", () => {
    const buttons = updateButtons(
      undefined,
      status({ status: "update_available" }),
      idle,
    );

    expect(enabled(buttons)).toEqual([false, false, false]);
  });

  it("makes the next step the primary button, and Check now never", () => {
    const primary = (
      state: UpdateStateOut,
      overrides: Partial<UpdateStatus> = {},
    ) => {
      const buttons = updateButtons(state, status(overrides), idle);
      return [
        buttons.check.primary,
        buttons.download.primary,
        buttons.apply.primary,
      ];
    };

    expect(primary(state())).toEqual([false, false, false]);
    expect(primary(state({ pending_version: "1.1.0" }))).toEqual([
      false,
      true,
      false,
    ]);
    expect(
      primary(state({ state: "downloading", pending_version: "1.1.0" })),
    ).toEqual([false, true, false]);
    expect(
      primary(
        state({
          state: "downloaded",
          downloaded: true,
          pending_version: "1.1.0",
        }),
        { status: "update_available" },
      ),
    ).toEqual([false, false, true]);
  });

  it("offers nothing when there is no tray to answer, however much is waiting", () => {
    const downloaded = updateButtons(
      state({
        state: "downloaded",
        downloaded: true,
        pending_version: "1.1.0",
        tray_running: false,
      }),
      status({ status: "update_available" }),
      idle,
    );
    const found = updateButtons(
      state({ pending_version: "1.1.0", tray_running: false }),
      status({ status: "update_available" }),
      idle,
    );

    expect(enabled(downloaded)).toEqual([false, false, false]);
    expect(enabled(found)).toEqual([false, false, false]);
  });
});
