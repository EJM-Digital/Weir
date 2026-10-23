import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ProcessingLibrary } from "../../lib/processing/libraries-api";
import type { LibrarySettings } from "../../lib/processing/library-mode-api";
import { useLibrarySettingsQuery } from "../../lib/processing/library-mode-queries";
import {
  jsonAnswer,
  savedFields,
  stubWeirApi,
  type StubbedApi,
  type StubbedRequest,
} from "../../test/stub-weir-api";
import { held } from "../../test/held";
import { LibrarySetupPanel } from "./library-setup-panel";

const SETTINGS_PATH = "/api/v1/processing/libraries/3/library-settings";
const SCHEDULE_PATH = "/api/v1/processing/libraries/3/library-schedule";
const PUT_SETTINGS = `PUT ${SETTINGS_PATH}`;
const POST_SCHEDULE = `POST ${SCHEDULE_PATH}`;
const CSRF = "csrf-token-for-the-test";

const workflow = { id: 3, name: "Movies", rule_set_id: 1 } as ProcessingLibrary;

const NOTHING_SAVED: LibrarySettings = {
  library_folders: ["D:\\Media\\Movies"],
  library_schedule_enabled: false,
  clean_hardlinked_files: false,
  skip_if_manager_would_redownload: true,
  keep_original_after_clean: false,
  originals_folder: "",
  library_rule_set_id: null,
};

/** The refusal the server sends when the daily clean would remove tracks nobody has agreed to lose (LibraryModeMapping.ConfirmationRequired). */
const CONFIRMATION_REQUIRED = {
  error: "confirm_final_removal_required",
  detail:
    "12 files, 31 tracks will be removed. Removed tracks are gone for good; getting one back means downloading the title again.",
  files_count: 12,
  tracks_count: 31,
  estimated_bytes_saved: 5_368_709_120,
  warnings: [],
};

let api: StubbedApi;
let stored: LibrarySettings;
const onClose = vi.fn();

type ScheduleBody = { enabled: boolean; confirm_final_removal: boolean };

/**
 * A server holding one library's setup: Weir's page reads it, the panel saves it, and what is saved is what the next
 * read returns. `schedule` answers the daily clean step.
 */
function serve(
  saved: Partial<LibrarySettings>,
  schedule?: (request: StubbedRequest) => unknown,
) {
  stored = { ...NOTHING_SAVED, ...saved };
  api = stubWeirApi({
    "GET /api/v1/auth/me": {
      user: { id: 1, role: "operator", username: "operator", app_theme: null },
    },
    "GET /api/v1/auth/csrf": { csrf_token: CSRF },
    "GET /api/v1/processing/rule-sets": [{ id: 1, name: "Movies default" }],
    [`GET ${SETTINGS_PATH}`]: () => stored,
    [PUT_SETTINGS]: (request: StubbedRequest) => {
      stored = { ...stored, ...savedFields(request) } as LibrarySettings;
      return stored;
    },
    [POST_SCHEDULE]:
      schedule ??
      ((request: StubbedRequest) => {
        stored = {
          ...stored,
          library_schedule_enabled: (request.body as ScheduleBody).enabled,
        };
        return stored;
      }),
  });
}

/** The panel as the Library page shows it: given the setup the server holds now, and given it again after each save. */
function Page() {
  const settings = useLibrarySettingsQuery(workflow.id);
  return settings.data ? (
    <LibrarySetupPanel
      workflow={workflow}
      settings={settings.data}
      onClose={onClose}
    />
  ) : null;
}

function wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return (
    <MemoryRouter>
      <QueryClientProvider client={qc}>{children}</QueryClientProvider>
    </MemoryRouter>
  );
}

async function openPanel() {
  render(<Page />, { wrapper });
  await screen.findByTestId("library-setup-panel");
  // The panel is read-only until the session says the person may change it.
  await waitFor(() =>
    expect(screen.getByTestId("library-rules-profile")).toBeEnabled(),
  );
}

const save = () => screen.getByRole("button", { name: "Save" });

function dailyClean() {
  return screen.getByRole("radiogroup", {
    name: /Check and clean once a day/,
  });
}

function switchTo(group: HTMLElement, state: "On" | "Off") {
  fireEvent.click(within(group).getByRole("radio", { name: state }));
}

function switchNamed(label: RegExp) {
  return screen.getByRole("radiogroup", { name: label });
}

beforeEach(() => {
  onClose.mockReset();
});

afterEach(() => {
  expect(api.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

describe("the folders", () => {
  it("removes a folder only on Save, keeping the others", async () => {
    serve({ library_folders: ["D:\\Media\\Movies", "E:\\More Movies"] });
    await openPanel();

    fireEvent.click(
      screen.getByRole("button", { name: "Remove D:\\Media\\Movies" }),
    );
    expect(screen.queryByText("D:\\Media\\Movies")).not.toBeInTheDocument();
    expect(screen.getByText("E:\\More Movies")).toBeInTheDocument();
    expect(api.to(PUT_SETTINGS)).toHaveLength(0);

    fireEvent.click(save());

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(api.to(PUT_SETTINGS)).toHaveLength(1);
    expect(api.to(PUT_SETTINGS)[0]!.body).toEqual({
      library_folders: ["E:\\More Movies"],
      clean_hardlinked_files: false,
      skip_if_manager_would_redownload: true,
      keep_original_after_clean: false,
      originals_folder: "",
      library_rule_set_id: null,
      csrf_token: CSRF,
    });
    expect(api.to(POST_SCHEDULE)).toHaveLength(0);
  });

  it("saves an empty list when the last folder is removed, and says there is nothing to show", async () => {
    serve({});
    await openPanel();

    fireEvent.click(
      screen.getByRole("button", { name: "Remove D:\\Media\\Movies" }),
    );
    expect(
      screen.getByText(
        "No folders yet, so there is nothing to show for this library.",
      ),
    ).toBeInTheDocument();
    expect(
      within(dailyClean()).getByRole("radio", { name: "On" }),
    ).toBeDisabled();

    fireEvent.click(save());

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(api.to(PUT_SETTINGS)[0]!.body).toMatchObject({
      library_folders: [],
    });
  });
});

describe("while a save is on its way", () => {
  it("says Saving… and holds the button until the server answers", async () => {
    const answer = held<Response>();
    serve({});
    api.set(PUT_SETTINGS, () => answer.promise);
    await openPanel();
    fireEvent.click(
      screen.getByRole("button", { name: "Remove D:\\Media\\Movies" }),
    );

    fireEvent.click(save());

    const saving = await screen.findByRole("button", { name: "Saving…" });
    expect(saving).toBeDisabled();
    answer.resolve(jsonAnswer({ ...stored, library_folders: [] }));
    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
  });
});

describe("the daily clean", () => {
  it("is switched off without a confirmation, and nothing else is saved", async () => {
    serve({ library_schedule_enabled: true });
    await openPanel();
    expect(
      within(dailyClean()).getByRole("radio", { name: "On" }),
    ).toBeChecked();

    switchTo(dailyClean(), "Off");
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(api.to(POST_SCHEDULE)).toHaveLength(0);
    fireEvent.click(save());

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(api.to(POST_SCHEDULE)).toHaveLength(1);
    expect(api.to(POST_SCHEDULE)[0]!.body).toEqual({
      enabled: false,
      confirm_final_removal: false,
      csrf_token: CSRF,
    });
    expect(api.to(PUT_SETTINGS)).toHaveLength(0);
    expect(stored.library_schedule_enabled).toBe(false);
  });

  it("is switched on with the confirmation the person gave, after the folders it needs", async () => {
    serve({ library_folders: [] });
    await openPanel();
    expect(
      within(dailyClean()).getByRole("radio", { name: "On" }),
    ).toBeDisabled();

    fireEvent.change(screen.getByLabelText("Folder to add"), {
      target: { value: "D:\\Media\\Movies" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Add folder" }));
    switchTo(dailyClean(), "On");
    fireEvent.click(screen.getByRole("button", { name: "Switch it on" }));
    fireEvent.click(save());

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(
      api.requests
        .filter((request) => request.method !== "GET")
        .map((request) => `${request.method} ${request.path}`),
    ).toEqual([PUT_SETTINGS, POST_SCHEDULE]);
    expect(api.to(POST_SCHEDULE)[0]!.body).toEqual({
      enabled: true,
      confirm_final_removal: true,
      csrf_token: CSRF,
    });
  });

  it("shows the server's confirmation as the reason when it asks again, and stays open", async () => {
    serve({}, () => jsonAnswer(CONFIRMATION_REQUIRED, 400));
    await openPanel();

    switchTo(dailyClean(), "On");
    fireEvent.click(screen.getByRole("button", { name: "Switch it on" }));
    fireEvent.click(save());

    expect(
      await screen.findByTestId("library-setup-save-error"),
    ).toHaveTextContent(
      "12 files, 31 tracks will be removed. Removed tracks are gone for good; getting one back means downloading the title again.",
    );
    expect(onClose).not.toHaveBeenCalled();
    expect(stored.library_schedule_enabled).toBe(false);
  });

  it("shows the server's reason when it refuses the daily clean, and stays open", async () => {
    serve({}, () =>
      jsonAnswer(
        { detail: "Add a library folder before switching it on." },
        409,
      ),
    );
    await openPanel();

    switchTo(dailyClean(), "On");
    fireEvent.click(screen.getByRole("button", { name: "Switch it on" }));
    fireEvent.click(save());

    expect(
      await screen.findByTestId("library-setup-save-error"),
    ).toHaveTextContent("Add a library folder before switching it on.");
    expect(onClose).not.toHaveBeenCalled();
    expect(save()).toBeEnabled();
  });
});

describe("the checks before a file is cleaned", () => {
  it("sends the skip switch as it was set, and nothing else changed", async () => {
    serve({ skip_if_manager_would_redownload: true });
    await openPanel();
    const skip = switchNamed(/^Skip a file when cleaning it would make/);
    expect(within(skip).getByRole("radio", { name: "On" })).toBeChecked();

    switchTo(skip, "Off");
    fireEvent.click(save());

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(api.to(PUT_SETTINGS)[0]!.body).toEqual({
      library_folders: ["D:\\Media\\Movies"],
      clean_hardlinked_files: false,
      skip_if_manager_would_redownload: false,
      keep_original_after_clean: false,
      originals_folder: "",
      library_rule_set_id: null,
      csrf_token: CSRF,
    });
  });

  it("sends the seeding switch as it was set, and nothing else changed", async () => {
    serve({ clean_hardlinked_files: false });
    await openPanel();
    const seeding = switchNamed(/^Clean a file that is still seeding/);
    expect(within(seeding).getByRole("radio", { name: "Off" })).toBeChecked();

    switchTo(seeding, "On");
    fireEvent.click(save());

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(api.to(PUT_SETTINGS)[0]!.body).toEqual({
      library_folders: ["D:\\Media\\Movies"],
      clean_hardlinked_files: true,
      skip_if_manager_would_redownload: true,
      keep_original_after_clean: false,
      originals_folder: "",
      library_rule_set_id: null,
      csrf_token: CSRF,
    });
  });

  it("has nothing to save once a switch is put back to what is saved", async () => {
    serve({});
    await openPanel();
    const seeding = switchNamed(/^Clean a file that is still seeding/);

    switchTo(seeding, "On");
    expect(save()).toBeEnabled();
    switchTo(seeding, "Off");

    expect(save()).toBeDisabled();
  });
});

describe("a save that is only half done", () => {
  it("says the other changes were saved when the daily clean is refused, and a second Save only tries the daily clean again", async () => {
    let refuse = true;
    serve({}, (request) => {
      if (refuse) {
        return jsonAnswer(
          { detail: "Add a library folder before switching it on." },
          409,
        );
      }
      stored = {
        ...stored,
        library_schedule_enabled: (request.body as ScheduleBody).enabled,
      };
      return stored;
    });
    await openPanel();

    fireEvent.change(screen.getByLabelText("Folder to add"), {
      target: { value: "E:\\More Movies" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Add folder" }));
    switchTo(dailyClean(), "On");
    fireEvent.click(screen.getByRole("button", { name: "Switch it on" }));
    fireEvent.click(save());

    const notice = await screen.findByTestId("library-setup-save-error");
    expect(notice).toHaveTextContent(
      "Your other changes were saved, but the daily clean was not switched on. Add a library folder before switching it on.",
    );
    expect(onClose).not.toHaveBeenCalled();
    expect(api.to(PUT_SETTINGS)).toHaveLength(1);
    expect(stored.library_folders).toEqual([
      "D:\\Media\\Movies",
      "E:\\More Movies",
    ]);

    // The page reads the setup again once the folders are saved, so Save now only has the daily clean left.
    await waitFor(() => expect(api.to(`GET ${SETTINGS_PATH}`)).toHaveLength(2));
    refuse = false;
    fireEvent.click(save());

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(api.to(PUT_SETTINGS)).toHaveLength(1);
    expect(api.to(POST_SCHEDULE)).toHaveLength(2);
    expect(stored.library_schedule_enabled).toBe(true);
  });

  it("says the other changes were saved when switching the daily clean off fails", async () => {
    serve({ library_schedule_enabled: true }, () =>
      jsonAnswer({ detail: "The schedule is being changed." }, 409),
    );
    await openPanel();

    switchTo(dailyClean(), "Off");
    switchTo(switchNamed(/^Clean a file that is still seeding/), "On");
    fireEvent.click(save());

    expect(
      await screen.findByTestId("library-setup-save-error"),
    ).toHaveTextContent(
      "Your other changes were saved, but the daily clean was not switched off. The schedule is being changed.",
    );
  });

  it("gives the server's reason alone when nothing else had been saved", async () => {
    serve({}, () =>
      jsonAnswer({ detail: "The schedule is being changed." }, 409),
    );
    await openPanel();

    switchTo(dailyClean(), "On");
    fireEvent.click(screen.getByRole("button", { name: "Switch it on" }));
    fireEvent.click(save());

    expect(
      await screen.findByTestId("library-setup-save-error"),
    ).toHaveTextContent(/^The schedule is being changed\.$/);
    expect(api.to(PUT_SETTINGS)).toHaveLength(0);
  });
});
