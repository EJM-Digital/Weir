import { fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import {
  CEILING,
  TEST_CEILING_MS,
  libraryListReread,
  pendingSettings,
  renderWizard,
  WizardServer,
} from "./wizard-test-server";

vi.setConfig({ testTimeout: TEST_CEILING_MS });

afterEach(() => {
  vi.unstubAllGlobals();
});

const LIBRARIES = "/api/v1/processing/libraries";
const SETTINGS = "/api/v1/suite/settings";

function chooseNeither() {
  fireEvent.click(
    screen.getByRole("radio", { name: "Neither – I'll pick folders myself" }),
  );
}

function type(name: string, value: string) {
  fireEvent.change(screen.getByRole("textbox", { name }), {
    target: { value },
  });
}

function finish() {
  fireEvent.click(screen.getByRole("button", { name: "Finish setup" }));
}

async function openedWizard() {
  const view = renderWizard();
  await screen.findByRole("button", { name: "Finish setup" }, CEILING);
  return view;
}

describe("Finish refuses a watched folder with no output folder", () => {
  it.each([
    {
      type: "TV",
      watched: "TV watched folder",
      other: ["Movies watched folder", "Movies output folder"],
      message:
        "Add a TV output folder: it is needed when a TV watched folder is set.",
    },
    {
      type: "Movies",
      watched: "Movies watched folder",
      other: ["TV watched folder", "TV output folder"],
      message:
        "Add a Movies output folder: it is needed when a Movies watched folder is set.",
    },
  ])(
    "says so for $type and sends nothing",
    async ({ watched, other, message }) => {
      const server = new WizardServer();
      await openedWizard();
      chooseNeither();
      type(watched, "D:\\Downloads\\Watched");
      type(other[0], "D:\\Downloads\\Other");
      type(other[1], "E:\\Ready\\Other");

      finish();

      expect(await screen.findByRole("alert", {}, CEILING)).toHaveTextContent(
        message,
      );
      expect(server.changes()).toEqual([]);
      expect(
        screen.queryByRole("heading", { name: "What's next" }),
      ).not.toBeInTheDocument();
    },
  );

  it("counts an output folder of only spaces as missing", async () => {
    const server = new WizardServer();
    await openedWizard();
    chooseNeither();
    type("Movies watched folder", "D:\\Movies");
    type("Movies output folder", "   ");

    finish();

    expect(await screen.findByRole("alert", {}, CEILING)).toHaveTextContent(
      "Add a Movies output folder",
    );
    expect(server.changes()).toEqual([]);
  });

  it("asks about TV first when both are missing an output folder", async () => {
    const server = new WizardServer();
    await openedWizard();
    chooseNeither();
    type("Movies watched folder", "D:\\Movies");
    type("TV watched folder", "D:\\TV");

    finish();

    expect(await screen.findByRole("alert", {}, CEILING)).toHaveTextContent(
      "Add a TV output folder",
    );
    expect(server.changes()).toEqual([]);
  });

  it("saves once the output folder is added, and clears the refusal", async () => {
    const server = new WizardServer();
    await openedWizard();
    chooseNeither();
    type("TV watched folder", "D:\\TV");
    finish();
    await screen.findByRole("alert", {}, CEILING);

    type("TV output folder", "E:\\TVOut");
    finish();

    await screen.findByRole("heading", { name: "What's next" }, CEILING);
    expect(server.changesTo("POST", LIBRARIES)).toHaveLength(1);
    expect(server.changesTo("POST", LIBRARIES)[0].body).toEqual({
      name: "TV",
      media_type: "tv",
      watched_folder: "D:\\TV",
      output_folder: "E:\\TVOut",
    });
    expect(server.changesTo("PUT", SETTINGS)).toHaveLength(1);
  });

  it("lets a wizard with no folders at all finish", async () => {
    const server = new WizardServer();
    await openedWizard();
    chooseNeither();

    finish();

    await screen.findByRole("heading", { name: "What's next" }, CEILING);
    expect(server.changesTo("POST", LIBRARIES)).toEqual([]);
    expect(server.changesTo("PUT", SETTINGS)).toHaveLength(1);
  });
});

describe("Finish retried after the settings could not be saved", () => {
  it("updates the workflow the first attempt created instead of adding another", async () => {
    const server = new WizardServer({ refuseSettingsSaves: 1 });
    const { client } = await openedWizard();
    chooseNeither();
    type("Movies watched folder", "D:\\Movies");
    type("Movies output folder", "E:\\MoviesOut");

    finish();
    expect(await screen.findByRole("alert", {}, CEILING)).toBeInTheDocument();
    await libraryListReread(client, 1);
    type("Movies output folder", "F:\\MoviesReady");
    finish();

    await screen.findByRole("heading", { name: "What's next" }, CEILING);
    expect(server.changesTo("POST", LIBRARIES)).toHaveLength(1);
    const updates = server.changesTo("PUT", `${LIBRARIES}/`);
    expect(updates).toHaveLength(1);
    expect(updates[0].path).toBe(`${LIBRARIES}/1`);
    expect(updates[0].body).toMatchObject({
      name: "Movies",
      media_type: "movie",
      watched_folder: "D:\\Movies",
      output_folder: "F:\\MoviesReady",
    });
    expect(server.state.libraries).toHaveLength(1);
    expect(server.state.settings.setup_wizard_state).toBe("completed");
  });

  it("leaves the created workflow as it is when nothing changed, and saves the settings", async () => {
    const server = new WizardServer({ refuseSettingsSaves: 1 });
    const { client } = await openedWizard();
    chooseNeither();
    type("TV watched folder", "D:\\TV");
    type("TV output folder", "E:\\TVOut");

    finish();
    expect(await screen.findByRole("alert", {}, CEILING)).toBeInTheDocument();
    await libraryListReread(client, 1);
    finish();

    await screen.findByRole("heading", { name: "What's next" }, CEILING);
    expect(server.changesTo("POST", LIBRARIES)).toHaveLength(1);
    expect(server.changesTo("PUT", `${LIBRARIES}/`)).toEqual([]);
    expect(server.changesTo("PUT", SETTINGS)).toHaveLength(2);
    expect(server.state.libraries).toHaveLength(1);
  });

  it("does not mark the wizard complete on the refused save", async () => {
    const server = new WizardServer({ refuseSettingsSaves: 1 });
    await openedWizard();
    chooseNeither();

    finish();

    expect(await screen.findByRole("alert", {}, CEILING)).toBeInTheDocument();
    expect(server.state.settings.setup_wizard_state).toBe("pending");
    expect(
      screen.queryByRole("heading", { name: "What's next" }),
    ).not.toBeInTheDocument();
  });
});

describe("the time zone", () => {
  async function savedZone(server: WizardServer) {
    await screen.findByRole("heading", { name: "What's next" }, CEILING);
    const saves = server.changesTo("PUT", SETTINGS);
    expect(saves).toHaveLength(1);
    return saves[0].body?.app_timezone;
  }

  it("saves the zone picked in the list as app_timezone", async () => {
    const server = new WizardServer();
    await openedWizard();
    chooseNeither();

    fireEvent.click(screen.getByRole("button", { name: "Time zone" }));
    fireEvent.click(screen.getByRole("option", { name: /Australia — Perth/ }));
    finish();

    expect(await savedZone(server)).toBe("Australia/Perth");
    expect(server.state.settings.app_timezone).toBe("Australia/Perth");
  });

  it("shows the saved zone as chosen and keeps it when it is not changed", async () => {
    const server = new WizardServer({
      settings: pendingSettings({ app_timezone: "Europe/Berlin" }),
    });
    await openedWizard();
    chooseNeither();

    expect(screen.getByRole("button", { name: "Time zone" })).toHaveTextContent(
      /Germany — Berlin/,
    );
    finish();

    expect(await savedZone(server)).toBe("Europe/Berlin");
  });

  it("shows UTC as chosen when the server is in UTC, and saves it", async () => {
    const server = new WizardServer();
    await openedWizard();
    chooseNeither();

    expect(screen.getByRole("button", { name: "Time zone" })).toHaveTextContent(
      /^UTC — Coordinated Universal Time/,
    );
    finish();

    expect(await savedZone(server)).toBe("UTC");
  });

  it("shows a valid zone the list lacks as chosen, and keeps it when it is not changed", async () => {
    const server = new WizardServer({
      settings: pendingSettings({ app_timezone: "Europe/Dublin" }),
    });
    await openedWizard();
    chooseNeither();

    expect(screen.getByRole("button", { name: "Time zone" })).toHaveTextContent(
      /^Europe\/Dublin/,
    );
    finish();

    expect(await savedZone(server)).toBe("Europe/Dublin");
    expect(server.state.settings.app_timezone).toBe("Europe/Dublin");
  });

  it("keeps a saved zone with spaces around it, without them", async () => {
    const server = new WizardServer({
      settings: pendingSettings({ app_timezone: " Europe/Dublin " }),
    });
    await openedWizard();
    chooseNeither();

    finish();

    expect(await savedZone(server)).toBe("Europe/Dublin");
  });

  it.each(["", "   "])(
    "saves UTC when no zone is saved (%j)",
    async (saved) => {
      const server = new WizardServer({
        settings: pendingSettings({ app_timezone: saved }),
      });
      await openedWizard();
      chooseNeither();

      finish();

      expect(await savedZone(server)).toBe("UTC");
    },
  );

  it("lets another zone be picked over one the list lacks", async () => {
    const server = new WizardServer({
      settings: pendingSettings({ app_timezone: "Europe/Dublin" }),
    });
    await openedWizard();
    chooseNeither();

    fireEvent.click(screen.getByRole("button", { name: "Time zone" }));
    fireEvent.click(screen.getByRole("option", { name: /Japan — Tokyo/ }));
    finish();

    expect(await savedZone(server)).toBe("Asia/Tokyo");
  });
});
