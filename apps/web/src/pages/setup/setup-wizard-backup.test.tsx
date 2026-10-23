import { fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";

import { useFakeClock, useRealClock } from "../../test/fake-clock";
import {
  CEILING,
  TEST_CEILING_MS,
  renderWizard,
  WizardServer,
} from "../../test/wizard-test-server";

vi.setConfig({ testTimeout: TEST_CEILING_MS });

beforeEach(useFakeClock);

afterEach(() => {
  useRealClock();
  vi.unstubAllGlobals();
});

const SETTINGS = "/api/v1/suite/settings";

it("saves the usual backup time when the time is cleared", async () => {
  const server = new WizardServer();
  renderWizard();
  await screen.findByRole("button", { name: "Finish setup" }, CEILING);
  fireEvent.click(
    screen.getByRole("radio", { name: "Neither – I'll pick folders myself" }),
  );
  fireEvent.click(
    screen.getByRole("checkbox", {
      name: "Back up the configuration automatically",
    }),
  );
  const time = screen.getByLabelText("Preferred time") as HTMLInputElement;
  fireEvent.change(time, { target: { value: "03:30" } });
  expect(time.value).toBe("03:30");

  fireEvent.change(time, { target: { value: "" } });
  expect(time.value).toBe("02:00");

  fireEvent.click(screen.getByRole("button", { name: "Finish setup" }));

  await screen.findByRole("heading", { name: "What's next" }, CEILING);
  expect(server.changesTo("PUT", SETTINGS)[0].body).toMatchObject({
    configuration_backup_enabled: true,
    configuration_backup_preferred_time: "02:00",
  });
});
