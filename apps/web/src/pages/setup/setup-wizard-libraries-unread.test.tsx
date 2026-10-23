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

it("still lets setup be finished when the workflows could not be read, and changes none of them", async () => {
  const server = new WizardServer({ refuseLibraryReads: true });
  renderWizard();
  await screen.findByRole("button", { name: "Finish setup" }, CEILING);
  fireEvent.click(
    screen.getByRole("radio", { name: "Neither – I'll pick folders myself" }),
  );

  fireEvent.click(screen.getByRole("button", { name: "Finish setup" }));

  await screen.findByRole("heading", { name: "What's next" }, CEILING);
  expect(server.changesTo("POST", "/api/v1/processing/libraries")).toEqual([]);
  expect(server.changesTo("PUT", "/api/v1/suite/settings")).toHaveLength(1);
});
