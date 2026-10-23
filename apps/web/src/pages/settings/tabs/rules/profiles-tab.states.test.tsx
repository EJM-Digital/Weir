import {
  cleanup,
  fireEvent,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { jsonAnswer } from "../../../../test/stub-weir-api";
import {
  RECORDED_PROFILES,
  STORED,
  expectedSave,
  openProfiles,
  renderProfiles,
  savedBody,
  serveProfiles,
} from "../../../../test/profiles-test-support";

// The tab here is the real one, reading from a stand-in server at `fetch` that answers as the recorded replies did. The file
// is about what the Profiles tab shows around the rules themselves: with no profiles, who uses one, what a refused save
// leaves, the folds, and who may change anything.

const UPDATE = `PUT /api/v1/processing/rule-sets/${STORED.id}`;
const CREATE = "POST /api/v1/processing/rule-sets";
const NAME = "English feature films";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const nameField = () => screen.getByPlaceholderText(NAME);
const button = (name: string) => screen.getByRole("button", { name });
const unsavedPrompt = () => screen.queryByTestId("settings-unsaved-changes");

/** Opens the tab on a profile like the recorded one, with these rules changed from the start. */
async function openWith(changes: Record<string, unknown> = {}) {
  const api = serveProfiles([
    { ...STORED, ...changes },
    ...RECORDED_PROFILES.slice(1),
  ]);
  await openProfiles();
  return api;
}

describe("with no profiles", () => {
  it("says so, and offers New profile but no rules to edit", async () => {
    serveProfiles([]);

    renderProfiles();

    expect(await screen.findByText("No profiles yet")).toBeInTheDocument();
    expect(
      screen.getByText("Create one, then assign it under Workflows."),
    ).toBeInTheDocument();
    expect(button("New profile")).toBeEnabled();
    expect(screen.queryByRole("button", { name: "Save profile" })).toBeNull();
    expect(screen.queryByPlaceholderText(NAME)).toBeNull();
    expect(screen.queryByRole("heading", { name: "Audio" })).toBeNull();
  });
});

describe("the line under the profile's name", () => {
  it("names the workflows that use it", async () => {
    serveProfiles();

    await openProfiles();

    expect(screen.getByTestId("rule-set-used-by")).toHaveTextContent(
      "Used by Movies",
    );
  });

  it("says no workflow uses a profile that none has chosen", async () => {
    serveProfiles([
      ...RECORDED_PROFILES,
      { ...STORED, id: 7, name: "Unused rules", used_by_library_count: 0 },
    ]);
    await openProfiles();

    fireEvent.change(screen.getByRole("combobox", { name: "Profile" }), {
      target: { value: "7" },
    });

    await waitFor(() =>
      expect(screen.getByTestId("rule-set-used-by")).toHaveTextContent(
        "No workflow uses it yet. Choose it in a workflow's editor.",
      ),
    );
  });
});

describe("Save profile", () => {
  it("asks for a name and sends nothing when the name is cleared", async () => {
    const api = await openWith();

    fireEvent.change(nameField(), { target: { value: "" } });
    fireEvent.click(button("Save profile"));

    expect(await screen.findByRole("status")).toHaveTextContent(
      "Give this profile a name before saving it.",
    );
    expect(api.to(UPDATE)).toHaveLength(0);
  });

  it("says why when the server refuses the rules, keeps the edits, and can be pressed again", async () => {
    const api = await openWith();
    api.set(
      UPDATE,
      jsonAnswer(
        {
          detail:
            "Weir cannot keep this profile: the first audio language is not one it knows.",
        },
        400,
      ),
    );
    fireEvent.change(nameField(), { target: { value: "Anime" } });
    fireEvent.click(screen.getByRole("checkbox", { name: /Attachments/ }));

    fireEvent.click(button("Save profile"));

    expect(await screen.findByRole("status")).toHaveTextContent(
      "Weir cannot keep this profile: the first audio language is not one it knows.",
    );
    expect(nameField()).toHaveValue("Anime");
    expect(screen.getByRole("checkbox", { name: /Attachments/ })).toBeChecked();
    expect(button("Save profile")).toBeEnabled();
    expect(api.to(UPDATE)).toHaveLength(1);
  });
});

describe("the Also remove fold", () => {
  const fold = () =>
    screen.getByRole("heading", { name: "Also remove" }).closest("details")!;

  it("says Off and stays closed when nothing inside is on", async () => {
    await openWith();

    expect(fold()).not.toHaveAttribute("open");
    expect(fold()).toHaveTextContent("Off");
  });

  it("says how many are on, and opens by itself, when something inside is", async () => {
    await openWith({ remove_attachments: true, remove_title: true });

    expect(fold()).toHaveAttribute("open");
    expect(within(fold()).getByText("2 of 5 on")).toBeInTheDocument();
  });

  it.each([
    ["Attachments", "remove_attachments"],
    ["The file's built-in title", "remove_title"],
    ["Language tags", "remove_language_tags"],
    ["Other metadata", "remove_other_metadata"],
  ])("saves %s as %s when it is switched on", async (label, field) => {
    const api = await openWith();

    fireEvent.click(
      within(fold()).getByRole("checkbox", { name: new RegExp(label) }),
    );
    fireEvent.click(button("Save profile"));

    expect(await savedBody(api)).toEqual(expectedSave({ [field]: true }));
  });
});

describe("New profile", () => {
  it("asks first when the open profile has edits not saved, and Keep editing leaves it open", async () => {
    await openWith();
    fireEvent.change(nameField(), { target: { value: "Anime" } });

    fireEvent.click(button("New profile"));

    expect(unsavedPrompt()).toHaveTextContent(
      "You have unsaved changes to Movies rules. Leave without saving?",
    );
    fireEvent.click(screen.getByTestId("settings-unsaved-changes-cancel"));
    expect(unsavedPrompt()).toBeNull();
    expect(nameField()).toHaveValue("Anime");
    expect(screen.queryByRole("button", { name: "Create profile" })).toBeNull();
  });

  it("opens a blank profile once the person chooses to leave without saving", async () => {
    await openWith();
    fireEvent.change(nameField(), { target: { value: "Anime" } });

    fireEvent.click(button("New profile"));
    fireEvent.click(screen.getByTestId("settings-unsaved-changes-confirm"));

    expect(
      await screen.findByRole("button", { name: "Create profile" }),
    ).toBeVisible();
    expect(nameField()).toHaveValue("");
  });

  it("opens straight away when nothing is edited", async () => {
    await openWith();

    fireEvent.click(button("New profile"));

    expect(unsavedPrompt()).toBeNull();
    expect(
      await screen.findByRole("button", { name: "Create profile" }),
    ).toBeVisible();
  });

  it("leaves no way back to the saved profiles but leaving the tab: the profile list and New profile are gone, and there is no Cancel", async () => {
    await openWith();

    fireEvent.click(button("New profile"));

    await screen.findByRole("button", { name: "Create profile" });
    expect(screen.queryByRole("combobox", { name: "Profile" })).toBeNull();
    expect(screen.queryByRole("button", { name: "New profile" })).toBeNull();
    expect(screen.queryByRole("button", { name: /cancel/i })).toBeNull();
    expect(
      screen.getByText(
        "A new profile. Choose it in a workflow’s editor once it is saved.",
      ),
    ).toBeInTheDocument();
  });
});

describe("a viewer", () => {
  it("can read every rule but change none: each field, New profile, Save profile and Remove are off, and a press sends nothing", async () => {
    const api = serveProfiles();
    api.set("GET /api/v1/auth/me", {
      user: { id: 2, role: "viewer", username: "viewer", app_theme: null },
    });
    await openProfiles();
    await waitFor(() => expect(button("Save profile")).toBeDisabled());
    const workspace = screen.getByTestId("processing-rule-set-workspace");

    const profileList = screen.getByRole("combobox", { name: "Profile" });
    const fields = [
      ...workspace.querySelectorAll("input, select, textarea"),
    ].filter((field) => field !== profileList);
    expect(fields.length).toBeGreaterThan(15);
    for (const field of fields) {
      expect(
        field,
        field.getAttribute("aria-label") ?? field.outerHTML,
      ).toBeDisabled();
    }
    for (const picker of workspace.querySelectorAll(
      '[aria-haspopup="listbox"]',
    )) {
      expect(picker).toBeDisabled();
    }
    expect(button("New profile")).toBeDisabled();
    expect(button("Remove profile")).toBeDisabled();

    fireEvent.click(button("Save profile"));
    fireEvent.click(button("New profile"));
    expect(api.requests.filter((request) => request.method !== "GET")).toEqual(
      [],
    );
    expect(api.to(CREATE)).toHaveLength(0);
  });
});
