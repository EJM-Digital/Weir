import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { ProcessingRuntimeSettingsOut } from "../../../../lib/processing/types";
import type {
  AppSettings,
  UpdateSettingsOut,
  UpdateStatus,
} from "../../../../lib/settings/types";
import { systemReplies } from "../../../../test/recorded-system";
import {
  serveRecordedReplies,
  type RecordedReply,
  type RecordedRoutes,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import { testOverview } from "../../../processing/dashboard/system/test-stats";
import { csrfToken } from "../security/security-replies";
import { UpdatePreferences } from "./update-preferences";
import { MediaToolsSection } from "./media-tools-section";
import { ThisWeirSection } from "./this-weir-section";
import { UpdateSection } from "./update-section";

const READINESS = "GET /api/v1/system/readiness";
const OVERVIEW = "GET /api/v1/system/overview";
const UPDATE_STATUS = "GET /api/v1/suite/update-status";
const UPDATE_SETTINGS = "GET /api/v1/suite/update-settings";
const SAVE_UPDATE_SETTINGS = "PUT /api/v1/suite/update-settings";
const MEDIA_TOOLS = "GET /api/v1/system/media-tools";
const RUNTIME = "GET /api/v1/processing/runtime-settings";

/** The reply a route gives when the server can be reached and cannot do what was asked. */
const FAILS: RecordedReply = systemReplies.settingsPutFails;

const reply = (body: unknown): RecordedReply => ({
  status: 200,
  body: JSON.stringify(body),
});

const STATUS: UpdateStatus = {
  current_version: "3.2.16",
  install_type: "windows",
  status: "up_to_date",
  summary: "This install is already on Weir 3.2.16.",
  latest_version: "3.2.16",
  latest_name: "Weir 3.2.16",
  published_at: null,
  release_url: null,
  windows_installer_url: null,
  docker_image: null,
  docker_tag: null,
  docker_update_command: null,
  in_app_upgrade_supported: true,
  in_app_upgrade_summary: null,
};
const PREFERENCES: UpdateSettingsOut = {
  mode: "Auto",
  check_on_startup: true,
  check_interval_minutes: 60,
};
const SETTINGS = {
  setup_wizard_state: "completed",
} as AppSettings;

let weir: RecordedWeir;

function serve(routes: RecordedRoutes) {
  weir = serveRecordedReplies({
    "GET /api/v1/auth/csrf": csrfToken,
    ...routes,
  });
}

function show(ui: React.ReactElement) {
  return render(
    <QueryClientProvider
      client={
        new QueryClient({
          defaultOptions: {
            queries: { retry: false, staleTime: 0 },
            mutations: { retry: false },
          },
        })
      }
    >
      <MemoryRouter>{ui}</MemoryRouter>
    </QueryClientProvider>,
  );
}

/** The `dd` beside a fact's name. */
function fact(name: string): HTMLElement {
  const row = screen.getByText(name, { selector: "dt" }).parentElement;
  return within(row as HTMLElement).getByRole("definition");
}

afterEach(() => vi.unstubAllGlobals());

describe("This Weir: the version and what the update check says of it", () => {
  it.each([
    ["up_to_date", "Up to date", "done"],
    ["update_available", "Update available", "todo"],
    ["unavailable", "Unavailable", "attention"],
  ])(
    "shows the version beside a chip that reads %s as %s",
    async (status, label, meaning) => {
      serve({
        [UPDATE_STATUS]: reply({ ...STATUS, status }),
        [READINESS]: systemReplies.readiness,
      });

      show(<ThisWeirSection settings={SETTINGS} />);

      const version = await screen.findByText("3.2.16");
      const chip = within(version.parentElement as HTMLElement).getByText(
        label,
      );
      expect(chip.closest("[data-status]")).toHaveAttribute(
        "data-status",
        meaning,
      );
    },
  );

  it("shows the version Weir reports about itself, and no chip, until the update check has answered", async () => {
    serve({ [READINESS]: systemReplies.readiness });

    show(<ThisWeirSection settings={SETTINGS} />);

    const readiness = JSON.parse(systemReplies.readiness.body) as {
      version: string;
    };
    expect(await screen.findByText(readiness.version)).toBeVisible();
    expect(screen.queryByText("Up to date")).toBeNull();
  });
});

describe("This Weir: where it runs", () => {
  it.each([
    ["service", "Service"],
    ["app", "App"],
    ["docker", "Docker"],
  ] as const)(
    "says a Weir that runs as %s runs as %s",
    async (runsAs, words) => {
      serve({ [OVERVIEW]: reply({ ...testOverview, runs_as: runsAs }) });

      show(<ThisWeirSection settings={SETTINGS} />);

      await waitFor(() => expect(fact("Runs as")).toHaveTextContent(words));
    },
  );

  it("says how much data it holds and the address it answers on, with its port", async () => {
    serve({ [OVERVIEW]: reply(testOverview) });

    show(<ThisWeirSection settings={SETTINGS} />);

    await waitFor(() => expect(fact("Data")).toHaveTextContent("5.0 MB"));
    expect(fact("Address")).toHaveTextContent("192.168.1.5:9347");
  });

  it("says the address by its host alone when it carries no port", async () => {
    serve({
      [OVERVIEW]: reply({ ...testOverview, address: "http://weir-host/" }),
    });

    show(<ThisWeirSection settings={SETTINGS} />);

    await waitFor(() =>
      expect(fact("Address")).toHaveTextContent(/^weir-host$/),
    );
  });

  it("shows no facts about the machine until the overview has been read", async () => {
    serve({ [OVERVIEW]: FAILS });

    show(<ThisWeirSection settings={SETTINGS} />);

    await screen.findByText("This Weir");
    await waitFor(() =>
      expect(weir.sent("GET", "/api/v1/system/overview")).toHaveLength(1),
    );
    expect(screen.queryByText("Runs as")).toBeNull();
  });
});

describe("Media tools: what Weir writes with", () => {
  it("writes with FFmpeg alone when mkvmerge is not installed, and links to the speed settings", async () => {
    serve({
      [MEDIA_TOOLS]: systemReplies.mediaTools,
      [RUNTIME]: reply({
        in_process_processing_worker_count: 2,
        processing_media_extensions: [".mkv", ".mp4"],
      } as ProcessingRuntimeSettingsOut),
    });

    show(<MediaToolsSection />);

    await waitFor(() => expect(fact("Worker slots")).toHaveTextContent("2"));
    expect(fact("Writes with")).toHaveTextContent(/^FFmpeg$/);
    expect(fact("File types")).toHaveTextContent(".mkv .mp4");
    expect(
      within(fact("Worker slots")).getByRole("link", {
        name: "Setup › Performance › Speed",
      }),
    ).toHaveAttribute("href", "/setup/performance");
  });

  it("writes MKV files with mkvmerge, and the rest with FFmpeg, when mkvmerge is installed", async () => {
    const found = JSON.parse(systemReplies.mediaTools.body) as {
      ffmpeg: string;
    };
    serve({
      [MEDIA_TOOLS]: reply({
        ffmpeg: found.ffmpeg,
        mkvmerge: "mkvmerge v88.0 ('Æon') 64-bit",
      }),
    });

    show(<MediaToolsSection />);

    await waitFor(() =>
      expect(fact("Writes with")).toHaveTextContent(
        "mkvmerge (MKV), FFmpeg (others)",
      ),
    );
    expect(fact("mkvmerge")).toHaveTextContent("88.0");
  });

  it("shows no worker slots or file types until the runtime settings have been read", async () => {
    serve({ [MEDIA_TOOLS]: systemReplies.mediaTools, [RUNTIME]: FAILS });

    show(<MediaToolsSection />);

    await screen.findByText("9.0.1");
    await waitFor(() =>
      expect(
        weir.sent("GET", "/api/v1/processing/runtime-settings"),
      ).toHaveLength(1),
    );
    expect(screen.queryByText("Worker slots")).toBeNull();
    expect(screen.queryByText("File types")).toBeNull();
  });
});

describe("Updates: while Weir asks and when it cannot", () => {
  it("says it is checking for updates until the status arrives", async () => {
    serve({ [UPDATE_STATUS]: () => new Promise(() => undefined) });

    show(<UpdateSection />);

    expect(await screen.findByText("Checking for updates...")).toBeVisible();
  });

  it("says it could not check for updates when the status cannot be read", async () => {
    serve({ [UPDATE_STATUS]: FAILS });

    show(<UpdateSection />);

    expect(await screen.findByRole("alert")).toBeVisible();
    expect(screen.queryByText("Checking for updates...")).toBeNull();
    expect(screen.queryByTestId("suite-settings-release-status")).toBeNull();
  });
});

describe("Update preferences", () => {
  const preferencesFor = (current: UpdateSettingsOut = PREFERENCES) => ({
    [UPDATE_SETTINGS]: reply(current),
    [SAVE_UPDATE_SETTINGS]: (): Promise<RecordedReply> =>
      Promise.resolve(reply(current)),
  });
  const everyRequestBody = () =>
    weir.sent("PUT", "/api/v1/suite/update-settings").map((r) => r.body);

  it("says it is loading the preferences until they arrive", async () => {
    serve({ [UPDATE_SETTINGS]: () => new Promise(() => undefined) });

    show(<UpdatePreferences />);

    expect(
      await screen.findByText("Loading update preferences..."),
    ).toBeVisible();
  });

  it("says the preferences could not be loaded when the server cannot give them", async () => {
    serve({ [UPDATE_SETTINGS]: FAILS });

    show(<UpdatePreferences />);

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Weir couldn't load update preferences.",
    );
    expect(screen.queryByRole("radiogroup")).toBeNull();
  });

  it("saves Check on startup being switched off with the other choices as they were, and says so", async () => {
    serve(preferencesFor());
    show(<UpdatePreferences />);

    fireEvent.click(
      within(
        await screen.findByRole("radiogroup", {
          name: "Check for updates on startup",
        }),
      ).getByRole("radio", { name: "Off" }),
    );

    expect(await screen.findByText("Update settings saved.")).toBeVisible();
    expect(everyRequestBody()).toEqual([
      expect.objectContaining({
        mode: "Auto",
        check_on_startup: false,
        check_interval_minutes: 60,
      }),
    ]);
  });

  it("saves the interval chosen, in minutes", async () => {
    serve(preferencesFor());
    show(<UpdatePreferences />);

    fireEvent.change(await screen.findByLabelText("Check every"), {
      target: { value: "1440" },
    });

    expect(await screen.findByText("Update settings saved.")).toBeVisible();
    expect(everyRequestBody()).toEqual([
      expect.objectContaining({ check_interval_minutes: 1440 }),
    ]);
  });

  it("shows the choice being saved, with the controls off, until the server has answered", async () => {
    let answer: (r: RecordedReply) => void = () => undefined;
    serve({
      ...preferencesFor(),
      [SAVE_UPDATE_SETTINGS]: () =>
        new Promise<RecordedReply>((resolve) => (answer = resolve)),
    });
    show(<UpdatePreferences />);

    fireEvent.change(await screen.findByLabelText("Check every"), {
      target: { value: "360" },
    });

    await waitFor(() =>
      expect(screen.getByLabelText("Check every")).toBeDisabled(),
    );
    expect(screen.getByLabelText("Check every")).toHaveValue("360");
    answer(reply({ ...PREFERENCES, check_interval_minutes: 360 }));
    await waitFor(() =>
      expect(screen.getByLabelText("Check every")).toBeEnabled(),
    );
  });

  it("says the settings could not be saved, and no saved note, when the server fails", async () => {
    serve({ ...preferencesFor(), [SAVE_UPDATE_SETTINGS]: FAILS });
    show(<UpdatePreferences />);

    fireEvent.change(await screen.findByLabelText("Check every"), {
      target: { value: "360" },
    });

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Could not save update settings",
    );
    expect(screen.queryByText("Update settings saved.")).toBeNull();
  });
});
