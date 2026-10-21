import { fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { BackupsCard } from "./backups-card";

const NOW = Date.parse("2026-10-02T12:00:00Z");

type Settings = {
  configuration_backup_enabled: boolean;
  configuration_backup_interval_hours: number;
  configuration_backup_preferred_time: string;
  configuration_backup_last_run_at: string | null;
  app_timezone: string;
};
type Backup = {
  id: number;
  created_at: string;
  file_name: string;
  size_bytes: number;
};

const settings: { data: Settings | undefined } = { data: undefined };
const backups: { data: { items: Backup[] } | undefined; isSuccess: boolean } = {
  data: undefined,
  isSuccess: true,
};
const tools: { data: { ffmpeg: string; mkvmerge: string } | undefined } = {
  data: undefined,
};
const updateStatus: {
  data: Record<string, unknown> | undefined;
  isError: boolean;
} = { data: undefined, isError: false };
const updateState: { data: Record<string, unknown> | undefined } = {
  data: undefined,
};
const backUp = {
  isPending: false,
  isError: false,
  error: null as unknown,
  mutate: vi.fn(),
};
let fits = Number.MAX_SAFE_INTEGER;

vi.mock("../fit-rows", () => ({
  useFittingRows: () => [{ current: null }, fits],
}));
vi.mock("../../../../lib/settings/queries", () => ({
  useAppSettingsQuery: () => settings,
  useConfigurationBackupsQuery: () => backups,
  useUpdateStatusQuery: () => updateStatus,
  useUpdateStateQuery: () => updateState,
}));
vi.mock("../../../../lib/system/media-tools", async (importOriginal) => ({
  ...(await importOriginal<
    typeof import("../../../../lib/system/media-tools")
  >()),
  useMediaToolsQuery: () => tools,
}));
vi.mock("../../../../lib/ui/mm-format-date", async (importOriginal) => ({
  ...(await importOriginal<
    typeof import("../../../../lib/ui/mm-format-date")
  >()),
  useAppClockFormatter: () => (ms: number) => `at ${ms}`,
}));
vi.mock("./use-back-up-now", () => ({ useBackUpNow: () => backUp }));

function renderCard() {
  render(
    <MemoryRouter>
      <BackupsCard />
    </MemoryRouter>,
  );
  return screen.getByRole("region", { name: "Backups and tools" });
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(NOW);
  settings.data = {
    configuration_backup_enabled: true,
    configuration_backup_interval_hours: 24,
    configuration_backup_preferred_time: "03:00",
    configuration_backup_last_run_at: "2026-10-02T03:00:00Z",
    app_timezone: "UTC",
  };
  backups.data = {
    items: [
      {
        id: 1,
        created_at: "2026-10-01T03:00:00Z",
        file_name: "a.json",
        size_bytes: 2 * 1024 * 1024,
      },
      {
        id: 2,
        created_at: "2026-10-02T03:00:00Z",
        file_name: "b.json",
        size_bytes: 3 * 1024 * 1024,
      },
    ],
  };
  backups.isSuccess = true;
  tools.data = {
    ffmpeg: "ffmpeg version 7.1 Copyright",
    mkvmerge: "mkvmerge v88.0 ('Aeon') 64-bit",
  };
  updateStatus.data = {
    current_version: "3.2.16",
    latest_version: "3.3.0",
    status: "update_available",
  };
  updateStatus.isError = false;
  updateState.data = undefined;
  backUp.isPending = false;
  backUp.isError = false;
  backUp.mutate = vi.fn();
  fits = Number.MAX_SAFE_INTEGER;
});

afterEach(() => {
  vi.useRealTimers();
});

describe("the Backups and tools card", () => {
  it("says the schedule and when the next backup runs", () => {
    const card = renderCard();

    const head = within(card).getByTestId("system-backup-head");
    expect(head).toHaveTextContent("Every day at");
    expect(head).toHaveTextContent("next in 15 h");
  });

  it("lists the newest backups with their sizes, newest first", () => {
    const card = renderCard();

    const rows = within(card).getAllByTestId("system-backup");
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent("Today");
    expect(rows[0]).toHaveTextContent("3.0 MB");
    expect(rows[1]).toHaveTextContent("Yesterday");
  });

  it("makes a backup now from the button", () => {
    const card = renderCard();

    fireEvent.click(within(card).getByRole("button", { name: "Back up now" }));

    expect(backUp.mutate).toHaveBeenCalledTimes(1);
  });

  it("says it is backing up while the backup is made", () => {
    backUp.isPending = true;
    const card = renderCard();
    expect(
      within(card).getByRole("button", { name: "Backing up…" }),
    ).toBeDisabled();
  });

  it("says why a backup failed", () => {
    backUp.isError = true;
    backUp.error = new Error("The backup folder is full.");
    const card = renderCard();

    expect(within(card).getByRole("alert")).toHaveTextContent(
      "The backup folder is full.",
    );
  });

  it("says no backup has been made yet", () => {
    backups.data = { items: [] };
    const card = renderCard();

    expect(card).toHaveTextContent("No backup has been made yet.");
  });

  it("says automatic backups are off, with no next time", () => {
    settings.data = {
      ...(settings.data as Settings),
      configuration_backup_enabled: false,
    };
    const card = renderCard();

    const head = within(card).getByTestId("system-backup-head");
    expect(head).toHaveTextContent("Automatic backups are off");
    expect(head).not.toHaveTextContent("next in");
  });

  it("shows each tool with its version and a tick", () => {
    const card = renderCard();

    const rows = within(card).getAllByTestId("system-tool");
    expect(rows[0]).toHaveTextContent("FFmpeg");
    expect(rows[0]).toHaveTextContent("7.1");
    expect(within(rows[0]).getByLabelText("Installed")).toBeInTheDocument();
    expect(rows[1]).toHaveTextContent("mkvmerge");
    expect(rows[1]).toHaveTextContent("88.0");
  });

  it("says where updating stands, linked to System", () => {
    const card = renderCard();

    const updates = within(card).getByTestId("system-updates");
    expect(updates).toHaveTextContent("Running 3.2.16");
    expect(updates).toHaveTextContent("Latest 3.3.0");
    expect(updates).toHaveTextContent("Update available");
    expect(within(updates).getByRole("link")).toHaveAttribute(
      "href",
      "/system",
    );
  });

  it("says Weir could not check for updates when the read failed", () => {
    updateStatus.data = undefined;
    updateStatus.isError = true;
    const card = renderCard();

    expect(within(card).getByTestId("system-updates")).toHaveTextContent(
      "could not check for updates",
    );
  });

  it("says it is reading the tools until they are known", () => {
    tools.data = undefined;
    const card = renderCard();

    expect(within(card).getByTestId("system-tools")).toHaveTextContent(
      "Reading the tools…",
    );
    expect(within(card).queryAllByTestId("system-tool")).toHaveLength(0);
  });

  it("marks a tool that is not installed", () => {
    tools.data = {
      ffmpeg: "ffmpeg version 7.1 Copyright",
      mkvmerge: "not installed",
    };
    const card = renderCard();

    const missing = within(card).getAllByTestId("system-tool")[1];
    expect(within(missing).getByLabelText("Not installed")).toHaveTextContent(
      "—",
    );
  });

  it("says it is checking for updates until the answer comes", () => {
    updateStatus.data = undefined;
    const card = renderCard();

    expect(within(card).getByTestId("system-updates")).toHaveTextContent(
      "Checking…",
    );
  });

  it("names only the version running when it is the newest", () => {
    updateStatus.data = {
      current_version: "3.3.0",
      latest_version: "3.3.0",
      status: "up_to_date",
    };
    const card = renderCard();

    const updates = within(card).getByTestId("system-updates");
    expect(updates).toHaveTextContent("Running 3.3.0");
    expect(updates).toHaveTextContent("Up to date");
    expect(updates).not.toHaveTextContent("Latest");
  });

  it("says nothing of the schedule or the backups until they are known", () => {
    settings.data = undefined;
    backups.data = undefined;
    backups.isSuccess = false;
    const card = renderCard();

    expect(
      within(card).getByTestId("system-backup-head"),
    ).not.toHaveTextContent(/Every|Automatic|next in/);
    expect(card).not.toHaveTextContent("No backup has been made yet.");
    expect(within(card).queryAllByTestId("system-backup")).toHaveLength(0);
  });

  it("tells the times in the browser's time zone when none is chosen", () => {
    settings.data = {
      ...(settings.data as Settings),
      app_timezone: "",
    };
    const card = renderCard();

    expect(within(card).getAllByTestId("system-backup")[0]).toHaveTextContent(
      "Today",
    );
  });

  it("says how many parts the card's height left out", () => {
    fits = 2;
    const card = renderCard();

    // The heading, two backups, the tools and the updates are five parts; two fit.
    expect(within(card).getByTestId("system-more")).toHaveTextContent("3 more");
  });

  it("links the header to Backups", () => {
    const card = renderCard();

    expect(
      within(card).getByRole("link", { name: "Backups: Backups and tools" }),
    ).toHaveAttribute("href", "/system?tab=backups");
  });
});
