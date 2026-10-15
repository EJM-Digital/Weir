import { fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { MediaManagerConnection } from "../../lib/media-managers/media-managers-api";
import type { ProcessingLibrary } from "../../lib/processing/libraries-api";
import type { SuggestedLibrary } from "../../lib/processing/library-setup-api";
import { library } from "../settings/tabs/libraries/library-test-fixtures";
import {
  CEILING,
  TEST_CEILING_MS,
  libraryListReread,
  renderWizard,
  WizardServer,
} from "./wizard-test-server";
import { useFakeClock, useRealClock } from "../../test/fake-clock";

vi.setConfig({ testTimeout: TEST_CEILING_MS });

// The wizard settles its folders after a pause; the tests move the clock on instead of waiting for it.
beforeEach(useFakeClock);

afterEach(() => {
  useRealClock();
  vi.unstubAllGlobals();
});

const LIBRARIES = "/api/v1/processing/libraries";

const DELUNO: MediaManagerConnection = {
  id: 5,
  kind: "deluno",
  name: "Deluno",
  enabled: true,
  base_url: "http://localhost:5000",
  api_key_is_saved: true,
  webhook_secret_is_set: false,
  webhook_url_path: "/api/v1/intake/webhook/deluno",
  downloaded_scan_enabled: false,
  unsigned_webhook_warning: null,
  last_test_ok: true,
  last_test_at: "2026-09-29T10:00:00Z",
  last_test_detail: "Connected.",
};

/** A TV workflow Deluno reports that no empty workflow is waiting for, so saving it adds one. */
const NEW_TV: SuggestedLibrary = {
  library_id: null,
  name: "TV (2)",
  media_type: "tv",
  watched_folder: "C:\\Downloads\\Completed\\TV",
  output_folder: "E:\\Weir\\Ready\\TV",
  source_label: "Deluno",
  manager_connection_ids: [5],
};

function savedTv(over: Partial<ProcessingLibrary>): ProcessingLibrary {
  return library({
    id: 3,
    name: "Shows",
    media_type: "tv",
    watched_folder: "C:\\Downloads\\Completed\\TV",
    output_folder: "D:\\Old\\TVOut",
    manager_connection_ids: [],
    ...over,
  });
}

function serverOffering(
  offered: SuggestedLibrary,
  libraries: ProcessingLibrary[] = [],
  refuseSettingsSaves = 0,
) {
  return new WizardServer({
    libraries,
    managers: [DELUNO],
    suggestions: { libraries: [offered], notes: [] },
    refuseSettingsSaves,
  });
}

async function offeredWizard() {
  const view = renderWizard();
  await screen.findByRole("button", { name: "Finish setup" }, CEILING);
  fireEvent.click(screen.getByRole("radio", { name: "Deluno" }));
  await screen.findByTestId("setup-wizard-found", {}, CEILING);
  // The folders are checked once they settle; waiting for the answer leaves no request in flight when the test ends.
  await screen.findByText("✓ These folders check out.", {}, CEILING);
  return view;
}

function finish() {
  fireEvent.click(screen.getByRole("button", { name: "Finish setup" }));
}

describe("an offered workflow already sitting at its watched folder", () => {
  it.each([
    ["the same folder", "C:\\Downloads\\Completed\\TV"],
    ["a trailing backslash", "C:\\Downloads\\Completed\\TV\\"],
    ["a trailing slash", "C:\\Downloads\\Completed\\TV/"],
    ["different capitals", "c:\\DOWNLOADS\\completed\\tv"],
  ])("is updated, not added again, with %s", async (_, savedFolder) => {
    const server = serverOffering(NEW_TV, [
      savedTv({ watched_folder: savedFolder }),
    ]);
    await offeredWizard();

    finish();

    await screen.findByRole("heading", { name: "What's next" }, CEILING);
    expect(server.changesTo("POST", LIBRARIES)).toEqual([]);
    const updates = server.changesTo("PUT", `${LIBRARIES}/`);
    expect(updates).toHaveLength(1);
    expect(updates[0].path).toBe(`${LIBRARIES}/3`);
    expect(updates[0].body).toMatchObject({
      name: "Shows",
      media_type: "tv",
      watched_folder: "C:\\Downloads\\Completed\\TV",
      output_folder: "E:\\Weir\\Ready\\TV",
      manager_connection_ids: [5],
    });
  });

  it.each([
    [
      "another media type",
      savedTv({ media_type: "movie", name: "Films", id: 4 }),
    ],
    ["another folder", savedTv({ watched_folder: "C:\\Downloads\\Other\\TV" })],
  ])("is added as a new one next to %s", async (_, other) => {
    const server = serverOffering(NEW_TV, [other]);
    await offeredWizard();

    finish();

    await screen.findByRole("heading", { name: "What's next" }, CEILING);
    expect(server.changesTo("PUT", `${LIBRARIES}/`)).toEqual([]);
    expect(server.changesTo("POST", LIBRARIES)).toHaveLength(1);
    expect(server.changesTo("POST", LIBRARIES)[0].body).toEqual({
      name: "TV (2)",
      media_type: "tv",
      watched_folder: "C:\\Downloads\\Completed\\TV",
      output_folder: "E:\\Weir\\Ready\\TV",
      manager_connection_ids: [5],
    });
  });
});

describe("Finish retried after the settings could not be saved, with offered workflows", () => {
  it("fills in the workflow the first attempt added instead of adding it twice", async () => {
    const server = serverOffering(NEW_TV, [], 1);
    const { client } = await offeredWizard();

    finish();
    expect(await screen.findByRole("alert", {}, CEILING)).toBeInTheDocument();
    await libraryListReread(client, 1);
    finish();

    await screen.findByRole("heading", { name: "What's next" }, CEILING);
    expect(server.changesTo("POST", LIBRARIES)).toHaveLength(1);
    const updates = server.changesTo("PUT", `${LIBRARIES}/`);
    expect(updates).toHaveLength(1);
    expect(updates[0].path).toBe(`${LIBRARIES}/1`);
    expect(updates[0].body).toMatchObject({
      name: "TV (2)",
      watched_folder: "C:\\Downloads\\Completed\\TV",
      output_folder: "E:\\Weir\\Ready\\TV",
      manager_connection_ids: [5],
    });
    expect(server.state.libraries).toHaveLength(1);
    expect(server.state.settings.setup_wizard_state).toBe("completed");
  });
});
