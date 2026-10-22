import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { asBoolean, parseActivityDetail } from "./activity/detail";
import { isLikelyViteProxyUpstreamDown } from "./api/error-guards";
import { ApiHttpError } from "./api/client";
import { errorMessage } from "./api/error-message";
import { fetchProcessingOverviewStats } from "./processing/overview-stats-api";
import { processingFileStatusLabel } from "./processing/files-api";
import { useLibraryFilePreviewQuery } from "./processing/library-mode-queries";
import {
  previewTrackName,
  type TrackNamePreviewSample,
} from "./processing/track-name-preview";
import { setupAreaOfTab, setupTabPath } from "./settings/setup-areas";
import type { SetupTabId } from "./settings/setup-areas";
import { curatedTimezoneOptionsSorted } from "./settings/timezone-options";
import { updateMeaning } from "./settings/update-status";
import { fetchServerDirectories } from "./system/directory-browser-api";
import { toolVersion } from "./system/media-tools";
import { withFrame } from "./system/system-stats-model";
import { fetchSystemTasks } from "./system/system-tasks";
import type { SystemPoint, SystemStats } from "./system/system-stats-types";

const calls: string[] = [];
let body: unknown = {};

vi.mock("./api/client", async (importOriginal) => {
  const original = await importOriginal<typeof import("./api/client")>();
  return {
    ...original,
    apiFetch: vi.fn(async (path: string) => {
      calls.push(path);
      return { ok: true };
    }),
    requireOk: vi.fn(async () => undefined),
    readJson: vi.fn(async () => body),
  };
});

afterEach(() => {
  calls.length = 0;
  body = {};
  vi.restoreAllMocks();
  vi.unstubAllEnvs();
});

describe("what people are told when something fails", () => {
  it.each<[string, unknown, string]>([
    ["a blank string", "  ", "fallback"],
    ["a string", "Disk full", "Disk full"],
    ["a blank message", { message: " " }, "fallback"],
    ["a null message", { message: null }, "fallback"],
    ["no message", {}, "fallback"],
    ["a message", { message: "Nope" }, "Nope"],
    ["an answer from Weir", new ApiHttpError("/x", 500, "Broke"), "Broke"],
  ])("%s", (_name, error, expected) => {
    expect(errorMessage(error, "fallback")).toBe(expected);
  });

  it("does not guess at a dev proxy outside development", () => {
    const down = new ApiHttpError("/x", 500, "down");
    expect(isLikelyViteProxyUpstreamDown(down)).toBe(true);
    vi.stubEnv("DEV", false);
    expect(isLikelyViteProxyUpstreamDown(down)).toBe(false);
  });
});

describe("reading values Weir sends", () => {
  it.each<[unknown, boolean | null]>([
    [true, true],
    ["true", true],
    ["false", false],
    ["yes", null],
  ])("a boolean from %j", (value, expected) => {
    expect(asBoolean(value)).toBe(expected);
  });

  it.each<[string | null, boolean]>([
    ['{"outcome":"done"}', true],
    ["{broken", false],
    ["plain words", false],
    [null, false],
  ])("an activity detail of %j", (detail, isObject) => {
    expect(parseActivityDetail(detail) !== null).toBe(isObject);
  });

  it.each<[string, string]>([
    ["a tool that says its version", "ffmpeg version 7.1-full_build"],
    ["a short answer", "not installed"],
    ["a long unreadable answer", `${"x".repeat(60)}`],
  ])("a version line for %s", (_name, line) => {
    const shown = toolVersion(line);
    expect(shown.length).toBeLessThanOrEqual(41);
  });

  it("names an unknown file status as it was sent", () => {
    expect(
      processingFileStatusLabel({
        status: "brand_new",
        failure_class: null,
        skip_kind: null,
      } as never),
    ).toBe("brand_new");
  });

  it("treats an update state it has no word for as needing attention", () => {
    expect(updateMeaning("something_new")).toBe("attention");
  });
});

describe("requests", () => {
  it.each<[number | undefined, string]>([
    [undefined, "/api/v1/processing/overview-stats"],
    [7, "/api/v1/processing/overview-stats?window_days=7"],
  ])("overview figures for %s days", async (days, path) => {
    await fetchProcessingOverviewStats(days);
    expect(calls).toEqual([path]);
  });

  it.each<[string | null | undefined, string]>([
    [undefined, "/api/v1/system/directories"],
    [null, "/api/v1/system/directories"],
    ["  ", "/api/v1/system/directories"],
    [" /media ", "/api/v1/system/directories?path=%2Fmedia"],
  ])("browsing %j", async (path, expected) => {
    await fetchServerDirectories(path);
    expect(calls).toEqual([expected]);
  });

  it("shows no scheduled tasks when the reply cannot be read", async () => {
    body = "garbage";
    expect(await fetchSystemTasks()).toEqual([]);
  });
});

describe("a preview of one file's rules", () => {
  function wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={new QueryClient()}>
        {children}
      </QueryClientProvider>
    );
  }

  it.each<[string | null, number | null, string[]]>([
    [null, null, []],
    ["/media/a.mkv", null, ["/api/v1/processing/libraries/1/preview"]],
    ["/media/a.mkv", 4, ["/api/v1/processing/libraries/1/preview"]],
  ])("asks for %j under profile %j", async (path, profile, expected) => {
    const { result } = renderHook(
      () => useLibraryFilePreviewQuery(1, path, profile, true),
      { wrapper },
    );
    if (expected.length > 0) {
      await waitFor(() => expect(result.current.isFetched).toBe(true));
    }
    expect(calls.filter((path) => !path.includes("csrf"))).toEqual(expected);
  });
});

describe("track names", () => {
  const sample = (channels: number): TrackNamePreviewSample => ({
    language: "English",
    channels,
    codec: "AAC",
  });

  it.each<[number, string]>([
    [0, "English AAC"],
    [1, "English 1.0 AAC"],
    [2, "English 2.0 AAC"],
    [6, "English 5.1 AAC"],
  ])("a track with %i channels reads %j", (channels, expected) => {
    expect(
      previewTrackName("{language} {channels} {codec}", sample(channels)),
    ).toBe(expected);
  });
});

describe("setup areas", () => {
  it("refuses a tab no area holds", () => {
    expect(() => setupAreaOfTab("nope" as SetupTabId)).toThrow(
      "No setup area holds the tab nope.",
    );
    expect(() => setupTabPath("nope" as SetupTabId)).toThrow();
  });
});

describe("time zone choices", () => {
  it("labels a zone as UTC+0 when the browser cannot name its offset", () => {
    const real = Intl.DateTimeFormat;
    vi.spyOn(Intl, "DateTimeFormat").mockImplementation(
      (...args: ConstructorParameters<typeof Intl.DateTimeFormat>) => {
        const format = new real(...args);
        return {
          formatToParts: () => [],
          format: format.format.bind(format),
          resolvedOptions: format.resolvedOptions.bind(format),
        } as unknown as Intl.DateTimeFormat;
      },
    );
    expect(
      curatedTimezoneOptionsSorted().every((o) => o.label.endsWith("(UTC+0)")),
    ).toBe(true);
  });

  it("labels a zone as UTC+0 when the browser refuses it", () => {
    vi.spyOn(Intl, "DateTimeFormat").mockImplementation(() => {
      throw new RangeError("bad zone");
    });
    expect(
      curatedTimezoneOptionsSorted().every((o) => o.label.endsWith("(UTC+0)")),
    ).toBe(true);
  });
});

describe("the stats window", () => {
  const point = (at: string): SystemPoint => ({ at }) as SystemPoint;

  it("keeps a history point whose time it cannot read", () => {
    const stats = {
      window_s: 60,
      history: [point("not a time"), point("2026-10-02T12:00:00Z")],
    } as unknown as SystemStats;
    const next = withFrame(stats, {
      now: {},
      machine: {},
      drives: [],
      point: point("2026-10-02T12:00:05Z"),
    } as never);
    expect(next.history).toHaveLength(3);
  });
});
