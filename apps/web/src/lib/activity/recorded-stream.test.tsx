import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { stubActivityStream } from "../../test/fake-activity-stream";
import { problemsWith } from "../../test/openapi-shape";
import { recordedBody } from "../../test/recorded";
import { parseConnectionActivity } from "../connections/connection-activity";
import { LIVE_TOPIC_QUERIES, parseDataChanged } from "../live/live-topics";
import { parseServerHello } from "../live/server-hello";
import { parseSystemChecksFrame } from "../system/system-checks-frame";
import { parseSystemOverviewFrame } from "../system/system-overview-frame";
import { parseSystemStatsFrame } from "../system/system-stats-frame";
import { parseSystemTasksFrame } from "../system/system-tasks-frame";
import type { ProcessingFilesPage } from "../processing/files-api";
import { mergeLiveProgress } from "../processing/live-progress-merge";
import { processingKeys } from "../processing/query-keys";
import {
  useActivityStreamInvalidations,
  useLiveProgress,
} from "./use-activity-stream-invalidation";

type ProgressFrame = {
  files: {
    relative_path: string;
    status: string;
    stage: string | null;
    percent: number | null;
    message: string | null;
    removed_audio: string[];
    removed_subtitles: string[];
  }[];
};

type Frame = { event: string; data: Record<string, unknown> | unknown[] };

/** The frames a running Weir sent on its Activity stream, a few of each kind, as it sent them. */
const FRAMES = recordedBody<{ frames: Frame[] }>(
  "GET /api/v1/activity/stream",
).frames;

const framesOf = (event: string) =>
  FRAMES.filter((frame) => frame.event === event);
const text = (frame: Frame) => JSON.stringify(frame.data);

afterEach(() => vi.unstubAllGlobals());

describe("the frames Weir really sent on the Activity stream", () => {
  it("include one of every kind the page listens for", () => {
    expect([...new Set(FRAMES.map((frame) => frame.event))]).toEqual(
      expect.arrayContaining([
        "server.hello",
        "activity.latest",
        "data.changed",
        "processing.progress",
        "connection.activity",
        "system.stats",
        "system.tasks",
        "system.overview",
        "system.checks",
      ]),
    );
  });

  it("fit the shapes the server's document gives them", () => {
    const documented: Record<string, string> = {
      "server.hello": "ServerHelloFrame",
      "data.changed": "DataChangedFrame",
      "connection.activity": "ConnectionActivityFrame",
      "system.stats": "SystemStatsFrame",
      "system.checks": "SystemChecksFrame",
      "system.log": "SystemLogFrame",
    };
    for (const frame of FRAMES) {
      const name = documented[frame.event];
      if (!name) continue;
      expect(
        problemsWith({ $ref: `#/components/schemas/${name}` }, frame.data),
        frame.event,
      ).toEqual([]);
    }
  });

  it("are understood by the page: the run's id, each topic, and each system reading", () => {
    const [hello] = framesOf("server.hello");
    expect(parseServerHello(text(hello!))).toBe(
      (hello!.data as { boot_id: string }).boot_id,
    );
    for (const frame of framesOf("data.changed")) {
      const topic = (frame.data as { topic: string }).topic;
      expect(parseDataChanged(text(frame))).toBe(topic);
      expect(Object.keys(LIVE_TOPIC_QUERIES)).toContain(topic);
    }
    for (const frame of framesOf("connection.activity")) {
      expect(parseConnectionActivity(text(frame))).not.toBeNull();
    }
    for (const frame of framesOf("system.stats")) {
      expect(parseSystemStatsFrame(text(frame))).not.toBeNull();
    }
    for (const frame of framesOf("system.tasks")) {
      expect(parseSystemTasksFrame(text(frame))?.length).toBe(
        (frame.data as unknown[]).length,
      );
    }
    for (const frame of framesOf("system.overview")) {
      expect(parseSystemOverviewFrame(text(frame))).not.toBeNull();
    }
    for (const frame of framesOf("system.checks")) {
      expect(parseSystemChecksFrame(text(frame))).not.toBeNull();
    }
  });

  it("reach the page through the stream: a new Activity entry refreshes what shows it", async () => {
    const stream = stubActivityStream();
    const client = new QueryClient();
    const invalidated = vi.spyOn(client, "invalidateQueries");
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
    renderHook(() => useActivityStreamInvalidations([processingKeys.files]), {
      wrapper,
    });

    const [latest] = framesOf("activity.latest");
    stream.frame(latest!.event, latest!.data);

    await waitFor(() => expect(invalidated).toHaveBeenCalled());
  });

  it("reach the page through the stream: a pass in progress is read as sent, and ends when the stream says nothing is in progress", () => {
    const stream = stubActivityStream();
    const { result } = renderHook(() => useLiveProgress());
    const [working] = framesOf("processing.progress").filter(
      (frame) => (frame.data as ProgressFrame).files.length > 0,
    );
    const [file] = (working!.data as ProgressFrame).files;

    act(() => stream.frame(working!.event, working!.data));

    expect(Object.keys(result.current)).toEqual([file!.relative_path]);
    expect(result.current[file!.relative_path]).toMatchObject({
      status: file!.status,
      stage: file!.stage,
      percent: file!.percent,
      message: file!.message,
      removedAudio: file!.removed_audio,
      removedSubtitles: file!.removed_subtitles,
    });
    act(() => stream.frame("processing.progress", { files: [] }));
    expect(result.current).toEqual({});
  });

  it("show a file the list calls waiting as processing, and leave the rest of the recorded file as it was", () => {
    const stream = stubActivityStream();
    const { result } = renderHook(() => useLiveProgress());
    const [working] = framesOf("processing.progress").filter(
      (frame) => (frame.data as ProgressFrame).files.length > 0,
    );
    const [file] = (working!.data as ProgressFrame).files;
    const [recorded] = recordedBody<ProcessingFilesPage>(
      "GET /api/v1/processing/files",
    ).files;
    const waiting = {
      ...recorded!,
      relative_path: file!.relative_path,
      status: "unprocessed" as const,
    };

    act(() => stream.frame(working!.event, working!.data));
    const [merged] = mergeLiveProgress([waiting], result.current);

    expect(merged).toMatchObject({
      status: "processing",
      size_bytes: recorded!.size_bytes,
      progress_stage: file!.stage,
      progress_percent: file!.percent,
    });
  });
});
