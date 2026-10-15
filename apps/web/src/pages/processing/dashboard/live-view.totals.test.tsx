import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { passFinished } from "../../../test/dashboard-fixtures";
import {
  stubActivityStream,
  type ActivityStreamHandle,
} from "../../../test/fake-activity-stream";
import {
  liveViewRoutes,
  quietWorld,
  type LiveWorld,
} from "../../../test/live-view-server";
import { stubWeirApi, type StubbedApi } from "../../../test/stub-weir-api";
import { LiveView } from "./live-view";

const LANES = "GET /api/v1/processing/files";
const STATS = "GET /api/v1/processing/overview-stats";
const AT_ONCE = "GET /api/v1/processing/files-at-once";
const ACTIVITY = "GET /api/v1/activity/recent";

/** A moment on a five-minute boundary, so the window the Today figures read does not move while a test advances the clock. */
const START = new Date("2026-10-15T10:00:00Z");
/** The lanes follow the stream at least this often, and the totals at least this seldom (live-view.tsx). */
const LANE_MS = 750;
const TOTAL_MS = 3_000;

let api: StubbedApi;
let stream: ActivityStreamHandle;
let world: LiveWorld;

async function settle() {
  for (let round = 0; round < 6; round++) {
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
  }
}

async function elapse(ms: number) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
  await settle();
}

async function show(workflowId: number | null = null) {
  api = stubWeirApi(liveViewRoutes(world));
  stream = stubActivityStream();
  render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <MemoryRouter>
        <LiveView
          filter="all"
          workflowId={workflowId}
          layout={{ sideBySide: true, band: "across" }}
        />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  await settle();
}

const cleanedToday = () => screen.getByTestId("live-done-today");
const asked = (route: string) => api.to(route).length;

beforeEach(() => {
  vi.useFakeTimers({
    toFake: [
      "setTimeout",
      "clearTimeout",
      "setInterval",
      "clearInterval",
      "Date",
    ],
  });
  vi.setSystemTime(START);
  world = quietWorld();
  world.stats.files_processed = 5;
  world.filesAtOnce.effective_files_at_once = 2;
});

afterEach(() => {
  expect(api.unmatched).toEqual([]);
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("the Today figure and the slots follow the Activity stream", () => {
  it("show what the server says when the page opens", async () => {
    await show();

    expect(cleanedToday()).toHaveTextContent("5");
    expect(screen.getByText("of 2 at once")).toBeInTheDocument();
  });

  it("change as soon as the stream says a pass wrote its entry, with no reload", async () => {
    await show();
    world.stats.files_processed = 6;
    world.filesAtOnce.effective_files_at_once = 3;

    stream.entryWritten(1);
    await settle();

    expect(cleanedToday()).toHaveTextContent("6");
    expect(screen.getByText("of 3 at once")).toBeInTheDocument();
  });

  it("are read again no sooner than every three seconds, while the lanes follow within under a second", async () => {
    await show();
    stream.entryWritten(1);
    await settle();
    const lanesBefore = asked(LANES);
    const statsBefore = asked(STATS);
    const slotsBefore = asked(AT_ONCE);
    world.stats.files_processed = 7;
    world.filesAtOnce.effective_files_at_once = 4;

    await elapse(1_000);
    stream.entryWritten(2);
    await settle();

    expect(asked(LANES)).toBeGreaterThan(lanesBefore);
    expect(asked(STATS)).toBe(statsBefore);
    expect(asked(AT_ONCE)).toBe(slotsBefore);
    expect(cleanedToday()).toHaveTextContent("5");

    await elapse(TOTAL_MS - 1_000 - 1);
    expect(asked(STATS)).toBe(statsBefore);
    expect(cleanedToday()).not.toHaveTextContent("7");

    await elapse(1);
    expect(asked(STATS)).toBe(statsBefore + 1);
    expect(asked(AT_ONCE)).toBe(slotsBefore + 1);
    expect(cleanedToday()).toHaveTextContent("7");
    expect(screen.getByText("of 4 at once")).toBeInTheDocument();
  });

  it("are read once, with the newest answer, for a burst of entries inside the three seconds", async () => {
    await show();
    stream.entryWritten(1);
    await settle();
    const statsBefore = asked(STATS);

    for (let id = 2; id <= 6; id++) {
      world.stats.files_processed = 5 + id;
      await elapse(100);
      stream.entryWritten(id);
    }
    await elapse(TOTAL_MS);

    expect(asked(STATS)).toBe(statsBefore + 1);
    expect(cleanedToday()).toHaveTextContent("11");
  });

  it("let the lanes be read again once 750 ms have passed since their last read", async () => {
    await show();
    stream.entryWritten(1);
    await settle();
    const lanesBefore = asked(LANES);

    await elapse(LANE_MS);
    stream.entryWritten(2);
    await settle();

    expect(asked(LANES)).toBeGreaterThan(lanesBefore);
  });
});

describe("the Today figure for one workflow follows the stream", () => {
  it("counts a pass that finished since it was last read", async () => {
    world.passes = [
      passFinished(11, "Harbour Lights (2024)/harbour.lights.mkv"),
    ];
    await show(1);
    expect(cleanedToday()).toHaveTextContent("1");
    const windowReads = asked(ACTIVITY);

    world.passes = [
      passFinished(12, "Old Films (1999)/old.film.1999.mkv"),
      ...world.passes,
    ];
    stream.entryWritten(1);
    await settle();

    expect(asked(ACTIVITY)).toBeGreaterThan(windowReads);
    expect(cleanedToday()).toHaveTextContent("2");
  });
});
