import { fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
  CEILING,
  TEST_CEILING_MS,
  renderWizard,
  WizardServer,
} from "../../test/wizard-test-server";
import { useFakeClock, useRealClock } from "../../test/fake-clock";

vi.setConfig({ testTimeout: TEST_CEILING_MS });

beforeEach(useFakeClock);

afterEach(() => {
  useRealClock();
  vi.unstubAllGlobals();
});

const SETTINGS = "/api/v1/suite/settings";
const UPDATE_SETTINGS = "/api/v1/suite/update-settings";

async function openedWizard() {
  const view = renderWizard();
  await screen.findByRole("button", { name: "Finish setup" }, CEILING);
  return view;
}

function chooseNeither() {
  fireEvent.click(
    screen.getByRole("radio", { name: "Neither – I'll pick folders myself" }),
  );
}

function finish() {
  fireEvent.click(screen.getByRole("button", { name: "Finish setup" }));
}

const updatesChoice = () =>
  screen.findByRole("checkbox", { name: "Check for updates" }, CEILING);

describe("Setup asks whether to check for updates, on the Windows app", () => {
  it("asks plainly, with the answer off on a new install", async () => {
    new WizardServer({ installType: "windows" });
    await openedWizard();

    expect(await updatesChoice()).not.toBeChecked();
    expect(
      screen.getByRole("heading", { name: "Updates" }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/It checks nothing unless you say so\./),
    ).toBeVisible();
  });

  it("saves a yes before it marks setup finished", async () => {
    const server = new WizardServer({ installType: "windows" });
    await openedWizard();
    chooseNeither();
    fireEvent.click(await updatesChoice());

    finish();

    await screen.findByRole("heading", { name: "What's next" }, CEILING);
    expect(server.changesTo("PUT", UPDATE_SETTINGS)).toEqual([
      expect.objectContaining({
        body: {
          mode: "Auto",
          check_on_startup: true,
          check_interval_minutes: 60,
          check_for_updates: true,
        },
      }),
    ]);
    const order = server.changes().map((request) => request.path);
    expect(order.indexOf(UPDATE_SETTINGS)).toBeLessThan(
      order.indexOf(SETTINGS),
    );
  });

  it("leaves checking off when the answer is no, and saves nothing for it", async () => {
    const server = new WizardServer({ installType: "windows" });
    await openedWizard();
    chooseNeither();
    await updatesChoice();

    finish();

    await screen.findByRole("heading", { name: "What's next" }, CEILING);
    expect(server.changesTo("PUT", UPDATE_SETTINGS)).toEqual([]);
    expect(server.state.updateSettings.check_for_updates).toBe(false);
  });

  it("saves a no to an answer that was yes, when setup is opened again", async () => {
    const server = new WizardServer({
      installType: "windows",
      updateSettings: {
        mode: "NotifyOnly",
        check_on_startup: false,
        check_interval_minutes: 240,
        check_for_updates: true,
      },
    });
    await openedWizard();
    chooseNeither();
    const choice = await updatesChoice();
    expect(choice).toBeChecked();

    fireEvent.click(choice);
    finish();

    await screen.findByRole("heading", { name: "What's next" }, CEILING);
    expect(server.state.updateSettings).toEqual({
      mode: "NotifyOnly",
      check_on_startup: false,
      check_interval_minutes: 240,
      check_for_updates: false,
    });
  });

  it("changes nothing about updates when setup is skipped", async () => {
    const server = new WizardServer({ installType: "windows" });
    await openedWizard();
    fireEvent.click(await updatesChoice());

    fireEvent.click(screen.getByTestId("setup-wizard-skip"));

    await vi.waitFor(() =>
      expect(server.changesTo("PUT", SETTINGS)).toHaveLength(1),
    );
    expect(server.changesTo("PUT", UPDATE_SETTINGS)).toEqual([]);
    expect(server.state.updateSettings.check_for_updates).toBe(false);
  });
});

describe("Setup does not ask about updates where Weir cannot hold the answer", () => {
  it.each(["docker", "source"])(
    "a %s install is not asked, and nothing about updates is read or saved",
    async (installType) => {
      const server = new WizardServer({ installType });
      await openedWizard();
      chooseNeither();

      finish();

      await screen.findByRole("heading", { name: "What's next" }, CEILING);
      expect(screen.queryByRole("heading", { name: "Updates" })).toBeNull();
      expect(
        server.sent.filter((request) => request.path === UPDATE_SETTINGS),
      ).toEqual([]);
    },
  );
});
