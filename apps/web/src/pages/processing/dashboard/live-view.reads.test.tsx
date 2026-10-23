import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import {
  liveViewRoutes,
  quietWorld,
  type LiveWorld,
} from "../../../test/live-view-server";
import {
  jsonAnswer,
  stubWeirApi,
  unexpectedProblem,
  type StubbedApi,
} from "../../../test/stub-weir-api";
import { held } from "../../../test/held";
import type { Filter } from "../processing-filter";
import { LiveView } from "./live-view";

/**
 * The Live view against a stubbed `fetch` when the server is slow or fails a read: it says it is loading, says what it
 * could not read, and never states as fact something it did not get to see.
 */

const FILES = "GET /api/v1/processing/files";
const STATS = "GET /api/v1/processing/overview-stats";
const ACTIVITY = "GET /api/v1/activity/recent";

let api: StubbedApi;

function show(
  world: LiveWorld,
  replies: Record<string, unknown> = {},
  filter: Filter = "all",
) {
  api = stubWeirApi({ ...liveViewRoutes(world), ...replies });
  render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <MemoryRouter initialEntries={["/"]}>
        <LiveView
          filter={filter}
          workflowId={null}
          layout={{ sideBySide: true, band: "across" }}
        />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const region = (name: string) => screen.findByRole("region", { name });

afterEach(() => {
  expect(api.unmatched).toEqual([]);
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
});

describe("the Live view while it waits for the files", () => {
  it("says it is loading the Dashboard and shows none of its cards", async () => {
    const files = held<Response>();
    show(quietWorld(), { [FILES]: () => files.promise });

    expect(await screen.findByText("Loading the Dashboard")).toBeVisible();
    expect(screen.queryByRole("region", { name: "Today" })).toBeNull();
    files.resolve(jsonAnswer({ files: [], total: 0 }));
  });
});

describe("the Live view while the workflows, the pause and the files-at-once setting are still being read", () => {
  it("draws the Dashboard from the files alone", async () => {
    const slow = () => new Promise<never>(() => undefined);
    show(quietWorld(), {
      "GET /api/v1/processing/libraries": slow,
      "GET /api/v1/pause": slow,
      "GET /api/v1/processing/files-at-once": slow,
    });

    expect(await region("Today")).toBeVisible();
    expect(screen.queryByText("Loading the Dashboard")).toBeNull();
  });
});

describe.each([
  ["download", "new downloads"],
  ["library", "library cleaning"],
] as const)("the Live view narrowed to %s work", (filter, name) => {
  it(`draws the Today figures for ${name} alone`, async () => {
    show(quietWorld(), {}, filter);

    expect(await region("Today")).toBeVisible();
    expect(screen.getByTestId("live-done-today")).toHaveTextContent("0");
  });

  it(`does not wait for the totals of new downloads to draw the figures for ${name}`, async () => {
    const slow = () => new Promise<never>(() => undefined);
    show(quietWorld(), { [STATS]: slow }, filter);

    expect(await region("Today")).toBeVisible();
  });
});

describe("the Live view when the files cannot be read", () => {
  it("says Weir hit a problem, with Reload, instead of drawing an empty Dashboard", async () => {
    vi.stubEnv("DEV", false);
    show(quietWorld(), { [FILES]: unexpectedProblem });

    expect(
      await screen.findByRole("heading", { name: "Weir hit a problem" }),
    ).toBeVisible();
    expect(screen.getByRole("button", { name: "Reload" })).toBeVisible();
    expect(screen.queryByRole("region", { name: "Pipeline" })).toBeNull();
  });

  it("says Weir cannot be reached when nothing answers", async () => {
    vi.stubEnv("DEV", false);
    const world = quietWorld();
    show(world, {
      [FILES]: () => {
        throw new TypeError("Failed to fetch");
      },
    });

    expect(
      await screen.findByRole("heading", { name: "Can't reach Weir" }),
    ).toBeVisible();
  });
});

describe("the Today tile when its figures cannot be read", () => {
  it("says it could not load them, never a figure and never a wait with no end", async () => {
    show(quietWorld(), { [STATS]: unexpectedProblem });

    const tile = await region("Today");

    expect(await within(tile).findByRole("alert")).toHaveTextContent(
      "Weir couldn't load today's figures. Reload the page to try again.",
    );
    expect(within(tile).getByTestId("live-done-today")).not.toHaveTextContent(
      "…",
    );
    expect(within(tile).getByTestId("live-done-today")).not.toHaveTextContent(
      /\d/,
    );
  });

  it("shows the count once the figures are read", async () => {
    const world = quietWorld();
    world.stats = { ...world.stats, files_processed: 38 };
    show(world);

    const tile = await region("Today");

    expect(await within(tile).findByText("38")).toBeVisible();
    expect(within(tile).queryByRole("alert")).toBeNull();
  });
});

describe("Just finished when the Activity read fails", () => {
  it("says it could not load what finished, not that nothing has", async () => {
    show(quietWorld(), { [ACTIVITY]: unexpectedProblem });

    const shelf = await region("Just finished");

    expect(await within(shelf).findByRole("alert")).toHaveTextContent(
      "Weir couldn't load what has finished. Reload the page to try again.",
    );
    expect(shelf).not.toHaveTextContent("Nothing has finished yet.");
  });

  it("says nothing has finished when the read worked and found nothing", async () => {
    show(quietWorld());

    const shelf = await region("Just finished");

    expect(
      await within(shelf).findByText("Nothing has finished yet."),
    ).toBeVisible();
    expect(within(shelf).queryByRole("alert")).toBeNull();
  });
});

describe("the Activity panel when the read fails", () => {
  it("says it could not load what Weir has done, not that nothing has happened", async () => {
    show(quietWorld(), { [ACTIVITY]: unexpectedProblem });

    const panel = await region("Activity");

    expect(await within(panel).findByRole("alert")).toHaveTextContent(
      "Weir couldn't load what Weir has done. Reload the page to try again.",
    );
    expect(panel).not.toHaveTextContent("Nothing yet.");
  });

  it("says nothing has happened yet when the read worked and found nothing", async () => {
    show(quietWorld());

    const panel = await region("Activity");

    expect(
      await within(panel).findByText(
        "Nothing yet. What Weir does appears here.",
      ),
    ).toBeVisible();
  });
});
