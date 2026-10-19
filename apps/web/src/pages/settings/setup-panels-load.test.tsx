import { QueryClient, focusManager } from "@tanstack/react-query";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { LIVE_TOPIC_QUERIES, type LiveTopic } from "../../lib/live/live-topics";
import { jsonAnswer } from "../../test/stub-weir-api";
import { LibrariesTab } from "./tabs/libraries/libraries-tab";
import { library } from "./tabs/libraries/library-test-fixtures";
import {
  serveWorkflows,
  workflowTabWrapper,
  type WorkflowServer,
} from "./tabs/libraries/workflow-server";
import { DevicesTab } from "./tabs/rules/devices-tab";
import { ProfilesTab } from "./tabs/rules/profiles-tab";
import { featureFilms } from "./tabs/rules/rule-set-fixtures";
import { ScheduleTab } from "./tabs/schedule/schedule-tab";

// Each panel here is the real one, reading from a stand-in server at `fetch`. The file is about the two moments a panel has
// no data of its own to show: while it is on its way, and after it did not arrive.

const SETTINGS = {
  setup_wizard_state: "completed",
  app_timezone: "UTC",
  log_retention_days: 30,
  activity_retention_days: 90,
  configuration_backup_enabled: false,
  configuration_backup_interval_hours: 24,
  configuration_backup_preferred_time: "02:00",
  configuration_backup_last_run_at: null,
  updated_at: "2026-04-11T00:00:00Z",
};

const DEVICES = {
  customised: false,
  devices: [
    {
      id: "apple_tv_4k",
      name: "Apple TV 4K",
      source: "https://www.apple.com/apple-tv-4k/specs/ (checked 2026-08-13)",
      note: "Apple's built-in player.",
      selected: true,
    },
  ],
};

const NEVER = () => new Promise<never>(() => undefined);
const DOWN = () => jsonAnswer({ detail: "Weir is not answering." }, 503);

type Panel = {
  name: string;
  render: () => React.JSX.Element;
  loading: string;
  /** The route this panel cannot show anything without. */
  route: string;
  /** What the server answers on that route once it is reachable. */
  answer: unknown;
  /** What the panel names in its failure line. */
  what: string;
  /** What the panel shows once it has its data. */
  shows: () => HTMLElement;
  /** The live topic that tells the page the data changed. */
  topic: LiveTopic;
  /** What the panel shows in place of its data when there is none yet; it must not appear while loading. */
  empty: RegExp;
};

const PANELS: Panel[] = [
  {
    name: "Workflows",
    render: () => <LibrariesTab />,
    loading: "Loading workflows",
    route: "GET /api/v1/processing/libraries",
    answer: [library({ id: 1, name: "Movies" })],
    what: "workflows",
    shows: () => screen.getByTestId("processing-library-1"),
    topic: "libraries",
    empty: /No workflows/,
  },
  {
    name: "Schedule",
    render: () => <ScheduleTab />,
    loading: "Loading schedules",
    route: "GET /api/v1/suite/settings",
    answer: SETTINGS,
    what: "schedules",
    shows: () => screen.getAllByTestId("schedule-library-row")[0]!,
    topic: "settings",
    empty: /Add a workflow under Workflows to schedule it here/,
  },
  {
    name: "Profiles",
    render: () => <ProfilesTab />,
    loading: "Loading profiles",
    route: "GET /api/v1/processing/rule-sets",
    answer: [featureFilms],
    what: "profiles",
    shows: () => screen.getByRole("heading", { name: "Audio" }),
    topic: "libraries",
    empty: /No profiles yet/,
  },
  {
    name: "Playback devices",
    render: () => <DevicesTab />,
    loading: "Loading your playback devices",
    route: "GET /api/v1/processing/direct-play/devices",
    answer: DEVICES,
    what: "playback devices",
    shows: () => screen.getByText("Apple TV 4K"),
    topic: "direct_play_devices",
    empty: /No devices are listed/,
  },
];

let server: WorkflowServer;
let client: QueryClient;

function serve(route: string, answer: unknown) {
  server = serveWorkflows({
    workflows: [library({ id: 1, name: "Movies" })],
    ruleSets: [featureFilms],
    extra: {
      "GET /api/v1/suite/settings": SETTINGS,
      "GET /api/v1/processing/direct-play/devices": DEVICES,
    },
  });
  server.set(route, answer);
}

/** The queries the live link reads again when the server says this topic changed. */
function liveSays(topic: LiveTopic) {
  return act(async () => {
    await Promise.all(
      LIVE_TOPIC_QUERIES[topic].map((queryKey) =>
        client.invalidateQueries({ queryKey }),
      ),
    );
  });
}

function show(panel: Panel) {
  client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, refetchOnWindowFocus: true },
      mutations: { retry: false },
    },
  });
  const Wrapper = workflowTabWrapper(client);
  render(<Wrapper>{panel.render()}</Wrapper>);
}

afterEach(() => {
  cleanup();
  focusManager.setFocused(undefined);
  vi.unstubAllGlobals();
});

describe.each(PANELS)("the $name panel", (panel) => {
  it("says it is loading while its data is on its way, and shows no empty state in its place", async () => {
    serve(panel.route, NEVER);

    show(panel);

    expect(await screen.findByText(panel.loading)).toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent(panel.loading);
    expect(screen.queryByText(panel.empty)).not.toBeInTheDocument();
    expect(screen.queryByTestId("settings-load-error")).not.toBeInTheDocument();
  });

  it("says Weir could not load it, with no Retry control, when its data did not arrive", async () => {
    serve(panel.route, DOWN);

    show(panel);

    expect(await screen.findByTestId("settings-load-error")).toHaveTextContent(
      `Weir couldn’t load your ${panel.what}. Reload the page to try again.`,
    );
    expect(screen.queryByText(panel.empty)).not.toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: /retry|try again/i }),
    ).not.toBeInTheDocument();
  });

  it("leaves the failure by itself when the live link says the data changed and it now arrives", async () => {
    serve(panel.route, DOWN);
    show(panel);
    await screen.findByTestId("settings-load-error");

    server.set(panel.route, panel.answer);
    await liveSays(panel.topic);

    await waitFor(() => expect(panel.shows()).toBeInTheDocument());
    expect(screen.queryByTestId("settings-load-error")).not.toBeInTheDocument();
  });

  it("leaves the failure by itself when the person returns to the tab and the data now arrives", async () => {
    serve(panel.route, DOWN);
    show(panel);
    await screen.findByTestId("settings-load-error");

    server.set(panel.route, panel.answer);
    act(() => {
      focusManager.setFocused(false);
      focusManager.setFocused(true);
    });

    await waitFor(() => expect(panel.shows()).toBeInTheDocument());
    expect(screen.queryByTestId("settings-load-error")).not.toBeInTheDocument();
  });
});
