import { useEffect } from "react";
import { useQueryClient, type QueryKey } from "@tanstack/react-query";
import { useSyncExternalStore } from "react";

import {
  reportLiveConnection,
  resetLiveConnection,
} from "../live/live-connection";
import {
  DATA_CHANGED_EVENT,
  parseDataChanged,
  type LiveTopic,
} from "../live/live-topics";
import { SERVER_HELLO_EVENT, parseServerHello } from "../live/server-hello";
import {
  CONNECTION_ACTIVITY_EVENT,
  parseConnectionActivity,
  type ConnectionActivityFrame,
} from "../connections/connection-activity";
import {
  SYSTEM_STATS_EVENT,
  parseSystemStatsFrame,
} from "../system/system-stats-frame";
import type {
  SystemOverview,
  SystemStatsFrame,
} from "../system/system-stats-types";
import {
  SYSTEM_LOG_EVENT,
  parseSystemLogFrame,
  type SystemLogFrame,
} from "../system/system-log-frame";
import {
  NO_LOOKS_YET,
  SYSTEM_CHECKS_EVENT,
  parseSystemChecksFrame,
  type ServerLooks,
} from "../system/system-checks-frame";
import {
  SYSTEM_OVERVIEW_EVENT,
  parseSystemOverviewFrame,
} from "../system/system-overview-frame";
import {
  SYSTEM_TASKS_EVENT,
  parseSystemTasksFrame,
  type SystemTask,
} from "../system/system-tasks-frame";

type LatestPayload = { latest_event_id: number; activity_revision?: number };
type ActivityLatestSubscriber = () => void;
type LiveProgressSubscriber = () => void;
type ConnectionActivitySubscriber = (frame: ConnectionActivityFrame) => void;
type SystemStatsSubscriber = (frame: SystemStatsFrame) => void;
type SystemTasksSubscriber = (tasks: SystemTask[]) => void;
type SystemOverviewSubscriber = (overview: SystemOverview) => void;
type SystemChecksSubscriber = () => void;
type SystemLogSubscriber = (frame: SystemLogFrame) => void;
type LiveSignalSubscriber = (signal: LiveSignal) => void;

/**
 * What the shared stream tells the app to catch up on: one kind of data changed, the connection came back after it was
 * lost, the server restarted since this page last heard from it, or the server refused the stream, which for a signed-in
 * page means the session has ended.
 */
export type LiveSignal =
  | { type: "changed"; topic: LiveTopic }
  | { type: "reconnected" }
  | { type: "restarted" }
  | { type: "refused" };

/** An EventSource whose readyState is this has given up for good and will not try again by itself. */
const EVENT_SOURCE_CLOSED = 2;

/** How long to wait before opening the stream again after the browser gave up on it; the server asks for the same. */
const REOPEN_DELAY_MS = 5_000;

/**
 * How long the stream may stay silent while the tab is showing before it is taken to be dead. A connection can die without
 * the browser noticing (a laptop that slept, a proxy that dropped it), and EventSource never reports that. The server sends
 * `system.stats` every second to every open stream, so a frame normally arrives at least that often. Keepalive comments, sent
 * every 16 s, are invisible to EventSource, which is why they cannot serve as the heartbeat. 40 s is more than twice the
 * keepalive and forty times the stats pace, so a slow server is never mistaken for a dead stream, yet a dead one is
 * noticed (within one {@link STREAM_WATCHDOG_TICK_MS} more) before what is on screen is long out of date.
 */
export const STREAM_SILENCE_LIMIT_MS = 40_000;

/** How often the watchdog looks at how long the stream has been silent. */
const STREAM_WATCHDOG_TICK_MS = 5_000;

/**
 * Never cancel a query that is already mid-flight just because a newer activity event arrived: the
 * in-flight answer is still good, and cancelling it only to refetch the same data again wastes a
 * request every time events arrive faster than a screen's own query can finish (#710).
 */
const INVALIDATE_OPTIONS = { cancelRefetch: false } as const;

/**
 * Marks these queries stale and reads them again. A read already in flight is kept rather than cancelled (#710), but it began
 * before whatever made the data stale, so its answer can be missing that very change; once every such read has landed, the
 * queries are read once more.
 */
export function invalidateLive(
  qc: ReturnType<typeof useQueryClient>,
  filters: { queryKey: QueryKey; exact?: boolean },
): void {
  const readingAlready = qc.isFetching(filters) > 0;
  void qc.invalidateQueries(filters, INVALIDATE_OPTIONS);
  if (!readingAlready) return;
  const stopWatching = qc.getQueryCache().subscribe(() => {
    if (qc.isFetching(filters) > 0) return;
    stopWatching();
    void qc.invalidateQueries(filters, INVALIDATE_OPTIONS);
  });
}

/** How far a running pass has got, straight from the `processing.progress` stream frame (#750). */
export type LiveProgressEntry = {
  relativePath: string;
  status: string;
  /** The step the pass names itself as being on; null from a server that does not say. */
  stage: string | null;
  percent: number | null;
  etaSeconds: number | null;
  message: string | null;
  speed: string | null;
  elapsedSeconds: number | null;
  removedAudio: string[];
  removedSubtitles: string[];
};

let source: EventSource | null = null;
let lastSeen: LatestPayload | null = null;
const subscribers = new Set<ActivityLatestSubscriber>();

const EMPTY_PROGRESS: Readonly<Record<string, LiveProgressEntry>> = {};
let liveProgressByPath: Readonly<Record<string, LiveProgressEntry>> =
  EMPTY_PROGRESS;
const progressSubscribers = new Set<LiveProgressSubscriber>();
const connectionActivitySubscribers = new Set<ConnectionActivitySubscriber>();
const systemStatsSubscribers = new Set<SystemStatsSubscriber>();
const systemTasksSubscribers = new Set<SystemTasksSubscriber>();
const systemOverviewSubscribers = new Set<SystemOverviewSubscriber>();
const systemChecksSubscribers = new Set<SystemChecksSubscriber>();
const systemLogSubscribers = new Set<SystemLogSubscriber>();
/** The newest times the server has said it looked, kept for a card that opens between two frames. */
let serverLooks: ServerLooks = NO_LOOKS_YET;
const liveSignalSubscribers = new Set<LiveSignalSubscriber>();
/** The run of the server the stream last said hello from; null until it has. */
let bootId: string | null = null;
let reopenTimer: number | null = null;
let watchdogTimer: number | null = null;
/** When the stream last sent anything the app listens for, or was opened. */
let lastFrameAt = 0;
/** A progress frame arrived while the tab was hidden and has not been shown yet. */
let progressChangedWhileHidden = false;

function emitActivityLatest(): void {
  subscribers.forEach((subscriber) => subscriber());
}

function emitLiveProgress(): void {
  progressSubscribers.forEach((subscriber) => subscriber());
}

function hasSubscribers(): boolean {
  return (
    subscribers.size > 0 ||
    progressSubscribers.size > 0 ||
    connectionActivitySubscribers.size > 0 ||
    systemStatsSubscribers.size > 0 ||
    systemTasksSubscribers.size > 0 ||
    systemOverviewSubscribers.size > 0 ||
    systemChecksSubscribers.size > 0 ||
    systemLogSubscribers.size > 0 ||
    liveSignalSubscribers.size > 0
  );
}

function tabIsHidden(): boolean {
  return (
    typeof document !== "undefined" && document.visibilityState === "hidden"
  );
}

function parseLatestPayload(data: string): LatestPayload | null {
  try {
    const parsed = JSON.parse(data) as Partial<LatestPayload>;
    if (typeof parsed.latest_event_id !== "number") {
      return null;
    }
    return {
      latest_event_id: parsed.latest_event_id,
      activity_revision:
        typeof parsed.activity_revision === "number"
          ? parsed.activity_revision
          : undefined,
    };
  } catch {
    return null;
  }
}

/**
 * Whether `payload` is something this connection hasn't already acted on: a higher event id, or the
 * same event id with a higher revision (an existing row changing in place). A stream replaying its
 * last message on reconnect, or two listeners inside one browser tab racing the same message, must
 * not invalidate every subscribed query a second time (#710).
 */
function isNewActivity(payload: LatestPayload): boolean {
  if (!lastSeen) return true;
  if (payload.latest_event_id !== lastSeen.latest_event_id) {
    return payload.latest_event_id > lastSeen.latest_event_id;
  }
  return (payload.activity_revision ?? 0) > (lastSeen.activity_revision ?? 0);
}

type RawProgressEntry = {
  relative_path?: unknown;
  status?: unknown;
  stage?: unknown;
  percent?: unknown;
  eta_seconds?: unknown;
  message?: unknown;
  speed?: unknown;
  elapsed_seconds?: unknown;
  removed_audio?: unknown;
  removed_subtitles?: unknown;
};

function number(value: unknown): number | null {
  return typeof value === "number" ? value : null;
}

function text(value: unknown): string | null {
  return typeof value === "string" ? value : null;
}

function strings(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((v) => typeof v === "string") : [];
}

/** The whole live-progress snapshot the stream carries every frame — never a diff (#750). */
function parseProgressPayload(data: string): LiveProgressEntry[] | null {
  try {
    const parsed = JSON.parse(data) as { files?: unknown };
    if (!Array.isArray(parsed.files)) return null;
    return (parsed.files as RawProgressEntry[]).flatMap((raw) => {
      const relativePath = text(raw.relative_path);
      if (relativePath === null) return [];
      return [
        {
          relativePath,
          status: text(raw.status) ?? "processing",
          stage: text(raw.stage),
          percent: number(raw.percent),
          etaSeconds: number(raw.eta_seconds),
          message: text(raw.message),
          speed: text(raw.speed),
          elapsedSeconds: number(raw.elapsed_seconds),
          removedAudio: strings(raw.removed_audio),
          removedSubtitles: strings(raw.removed_subtitles),
        },
      ];
    });
  } catch {
    return null;
  }
}

function emitLiveSignal(signal: LiveSignal): void {
  liveSignalSubscribers.forEach((subscriber) => subscriber(signal));
}

function stopWatchingStream(): void {
  source?.close();
  source = null;
  if (watchdogTimer !== null) {
    window.clearInterval(watchdogTimer);
    watchdogTimer = null;
  }
}

function closeActivityStream(): void {
  stopWatchingStream();
  if (reopenTimer !== null) {
    window.clearTimeout(reopenTimer);
    reopenTimer = null;
  }
}

function reopenActivityStreamSoon(): void {
  reopenTimer ??= window.setTimeout(() => {
    reopenTimer = null;
    ensureActivityStream();
  }, REOPEN_DELAY_MS);
}

/**
 * The stream dropped. The browser opens it again by itself, unless the server refused it outright; a refusal while signed
 * out ends with the shell going away, which closes the stream and cancels the reopen.
 */
function onStreamError(): void {
  reportLiveConnection("dropped");
  if (source?.readyState !== EVENT_SOURCE_CLOSED) return;
  stopWatchingStream();
  emitLiveSignal({ type: "refused" });
  reopenActivityStreamSoon();
}

/** Drops a stream that has gone quiet for too long while the tab is showing, and opens a fresh one shortly. */
function dropStreamIfSilent(): void {
  if (!source || tabIsHidden()) return;
  if (Date.now() - lastFrameAt < STREAM_SILENCE_LIMIT_MS) return;
  stopWatchingStream();
  reportLiveConnection("dropped");
  reopenActivityStreamSoon();
}

function onServerHello(data: string): void {
  const id = parseServerHello(data);
  if (id === null) return;
  const restarted = bootId !== null && bootId !== id;
  bootId = id;
  if (restarted) emitLiveSignal({ type: "restarted" });
}

/**
 * The connection stays open while the tab is hidden, so coming back to Weir shows what is true now
 * instead of catching up in front of you. Activity still refreshes each screen's data in the
 * background as it happens. Live progress, which changes every second, is only stored while hidden,
 * and is drawn once when the tab is shown again.
 */
function onVisibilityChange(): void {
  if (tabIsHidden()) return;
  dropStreamIfSilent();
  if (hasSubscribers()) {
    ensureActivityStream();
  }
  if (progressChangedWhileHidden) {
    progressChangedWhileHidden = false;
    emitLiveProgress();
  }
}

/**
 * The browser says the network is gone. A connection cut by that can look open for a long time, so the stream is not
 * trusted any more: it is dropped, and opened afresh (and every screen caught up) when the network is back.
 */
function onBrowserOffline(): void {
  if (!hasSubscribers()) return;
  closeActivityStream();
  reportLiveConnection("dropped");
}

function onBrowserOnline(): void {
  if (hasSubscribers()) ensureActivityStream();
}

let watchingBrowser = false;

function watchBrowser(): void {
  if (watchingBrowser || typeof document === "undefined") return;
  document.addEventListener("visibilitychange", onVisibilityChange);
  window.addEventListener("offline", onBrowserOffline);
  window.addEventListener("online", onBrowserOnline);
  watchingBrowser = true;
}

/** Wraps a handler of a stream frame so that hearing the frame, however it parses, shows the stream is alive. */
function hear(handler: (data: string) => void): (ev: Event) => void {
  return (ev) => {
    lastFrameAt = Date.now();
    handler((ev as MessageEvent<string>).data);
  };
}

function ensureActivityStream(): EventSource | null {
  if (source) {
    return source;
  }
  if (typeof EventSource === "undefined") {
    return null;
  }
  source = new EventSource("/api/v1/activity/stream");
  lastFrameAt = Date.now();
  watchdogTimer ??= window.setInterval(
    dropStreamIfSilent,
    STREAM_WATCHDOG_TICK_MS,
  );
  source.addEventListener("open", () => {
    if (reportLiveConnection("opened").reconnected) {
      emitLiveSignal({ type: "reconnected" });
    }
  });
  source.addEventListener("error", onStreamError);
  source.addEventListener(SERVER_HELLO_EVENT, hear(onServerHello));
  source.addEventListener(
    DATA_CHANGED_EVENT,
    hear((data) => {
      const topic = parseDataChanged(data);
      if (topic) emitLiveSignal({ type: "changed", topic });
    }),
  );
  source.addEventListener(
    "activity.latest",
    hear((data) => {
      const payload = parseLatestPayload(data);
      if (!payload || !isNewActivity(payload)) {
        return;
      }
      lastSeen = payload;
      emitActivityLatest();
    }),
  );
  source.addEventListener(
    "processing.progress",
    hear((data) => {
      const entries = parseProgressPayload(data);
      if (!entries) return;
      const next: Record<string, LiveProgressEntry> = {};
      for (const entry of entries) next[entry.relativePath] = entry;
      liveProgressByPath = next;
      if (tabIsHidden()) {
        progressChangedWhileHidden = true;
        return;
      }
      emitLiveProgress();
    }),
  );
  source.addEventListener(
    CONNECTION_ACTIVITY_EVENT,
    hear((data) => {
      const frame = parseConnectionActivity(data);
      if (frame) connectionActivitySubscribers.forEach((fn) => fn(frame));
    }),
  );
  source.addEventListener(
    SYSTEM_STATS_EVENT,
    hear((data) => {
      const frame = parseSystemStatsFrame(data);
      if (frame) systemStatsSubscribers.forEach((fn) => fn(frame));
    }),
  );
  source.addEventListener(
    SYSTEM_TASKS_EVENT,
    hear((data) => {
      const tasks = parseSystemTasksFrame(data);
      if (tasks) systemTasksSubscribers.forEach((fn) => fn(tasks));
    }),
  );
  source.addEventListener(
    SYSTEM_OVERVIEW_EVENT,
    hear((data) => {
      const overview = parseSystemOverviewFrame(data);
      if (overview) systemOverviewSubscribers.forEach((fn) => fn(overview));
    }),
  );
  source.addEventListener(
    SYSTEM_CHECKS_EVENT,
    hear((data) => {
      const looks = parseSystemChecksFrame(data);
      if (!looks) return;
      serverLooks = looks;
      systemChecksSubscribers.forEach((fn) => fn());
    }),
  );
  source.addEventListener(
    SYSTEM_LOG_EVENT,
    hear((data) => {
      const frame = parseSystemLogFrame(data);
      if (frame) systemLogSubscribers.forEach((fn) => fn(frame));
    }),
  );
  return source;
}

/** Closes the shared connection once nobody — invalidation, live progress or connection lights — still wants it. */
function closeIfNobodyIsWatching(): void {
  if (!hasSubscribers()) {
    closeActivityStream();
    lastSeen = null;
    bootId = null;
    resetLiveConnection();
    liveProgressByPath = EMPTY_PROGRESS;
    progressChangedWhileHidden = false;
  }
}

function subscribeActivityLatest(
  subscriber: ActivityLatestSubscriber,
): () => void {
  subscribers.add(subscriber);
  watchBrowser();
  ensureActivityStream();

  return () => {
    subscribers.delete(subscriber);
    closeIfNobodyIsWatching();
  };
}

function subscribeLiveProgress(subscriber: LiveProgressSubscriber): () => void {
  progressSubscribers.add(subscriber);
  watchBrowser();
  ensureActivityStream();

  return () => {
    progressSubscribers.delete(subscriber);
    closeIfNobodyIsWatching();
  };
}

/**
 * Calls `subscriber` with every `connection.activity` frame, on the one shared stream. A frame is a moment, not state:
 * nothing is kept for a subscriber that arrives late, and nothing is replayed to it.
 */
export function subscribeConnectionActivity(
  subscriber: ConnectionActivitySubscriber,
): () => void {
  connectionActivitySubscribers.add(subscriber);
  watchBrowser();
  ensureActivityStream();

  return () => {
    connectionActivitySubscribers.delete(subscriber);
    closeIfNobodyIsWatching();
  };
}

/**
 * Calls `subscriber` with every `system.stats` frame, on the one shared stream: the machine's newest reading, once a
 * second while any screen is listening. A frame is a moment, so nothing is replayed to a late subscriber.
 */
export function subscribeSystemStats(
  subscriber: SystemStatsSubscriber,
): () => void {
  systemStatsSubscribers.add(subscriber);
  watchBrowser();
  ensureActivityStream();

  return () => {
    systemStatsSubscribers.delete(subscriber);
    closeIfNobodyIsWatching();
  };
}

/**
 * Calls `subscriber` with the whole task list each time `system.tasks` says a task started or ended, on the one shared
 * stream. A frame is a moment, so nothing is replayed to a late subscriber.
 */
export function subscribeSystemTasks(
  subscriber: SystemTasksSubscriber,
): () => void {
  systemTasksSubscribers.add(subscriber);
  watchBrowser();
  ensureActivityStream();

  return () => {
    systemTasksSubscribers.delete(subscriber);
    closeIfNobodyIsWatching();
  };
}

/**
 * Calls `subscriber` with the overview of Weir itself each time `system.overview` says one of its facts changed, on the one
 * shared stream. A frame is a moment, so nothing is replayed to a late subscriber.
 */
export function subscribeSystemOverview(
  subscriber: SystemOverviewSubscriber,
): () => void {
  systemOverviewSubscribers.add(subscriber);
  watchBrowser();
  ensureActivityStream();

  return () => {
    systemOverviewSubscribers.delete(subscriber);
    closeIfNobodyIsWatching();
  };
}

/**
 * Calls `subscriber` each time a `system.checks` frame says the server looked again at what Health shows, on the one shared
 * stream. What it said is read with {@link getServerLooks}, which keeps the newest times for a subscriber that arrives later.
 */
export function subscribeSystemChecks(
  subscriber: SystemChecksSubscriber,
): () => void {
  systemChecksSubscribers.add(subscriber);
  watchBrowser();
  ensureActivityStream();

  return () => {
    systemChecksSubscribers.delete(subscriber);
    closeIfNobodyIsWatching();
  };
}

/** The newest times the server has said it looked, or none before its first frame. */
export function getServerLooks(): ServerLooks {
  return serverLooks;
}

/**
 * Calls `subscriber` with every `system.log` frame, a line as Weir writes it (a warning or error, or Weir's own information), on the one shared stream.
 * A frame is a moment, so nothing is replayed to a late subscriber.
 */
export function subscribeSystemLog(
  subscriber: SystemLogSubscriber,
): () => void {
  systemLogSubscribers.add(subscriber);
  watchBrowser();
  ensureActivityStream();

  return () => {
    systemLogSubscribers.delete(subscriber);
    closeIfNobodyIsWatching();
  };
}

/**
 * Calls `subscriber` with each thing the shared stream says the app must catch up on (see {@link LiveSignal}). A signal is a
 * moment, so nothing is replayed to a late subscriber.
 */
export function subscribeLiveSignals(
  subscriber: LiveSignalSubscriber,
): () => void {
  liveSignalSubscribers.add(subscriber);
  watchBrowser();
  ensureActivityStream();

  return () => {
    liveSignalSubscribers.delete(subscriber);
    closeIfNobodyIsWatching();
  };
}

function getLiveProgressSnapshot(): Readonly<
  Record<string, LiveProgressEntry>
> {
  return liveProgressByPath;
}

/**
 * Every file's live progress, straight from the shared `processing.progress` stream frame, updated at
 * most once a second (#750). Empty for a file that is not being worked on, or once its pass ends: the
 * snapshot the stream sends is the whole set in progress, not an accumulating log.
 */
export function useLiveProgress(): Readonly<Record<string, LiveProgressEntry>> {
  return useSyncExternalStore(
    subscribeLiveProgress,
    getLiveProgressSnapshot,
    getLiveProgressSnapshot,
  );
}

export function useActivityStreamInvalidations(
  queryKeys: readonly QueryKey[],
  options: { exact?: boolean; throttleMs?: number } = {},
): void {
  const qc = useQueryClient();
  const exact = options.exact ?? false;
  const throttleMs = Math.max(0, options.throttleMs ?? 0);

  useEffect(() => {
    let lastRunAt = 0;
    let trailingTimer: number | null = null;

    const invalidate = () => {
      lastRunAt = Date.now();
      queryKeys.forEach((queryKey) => invalidateLive(qc, { queryKey, exact }));
    };

    const unsubscribe = subscribeActivityLatest(() => {
      const remaining = throttleMs - (Date.now() - lastRunAt);
      if (remaining <= 0) {
        if (trailingTimer !== null) {
          window.clearTimeout(trailingTimer);
          trailingTimer = null;
        }
        invalidate();
        return;
      }

      if (trailingTimer === null) {
        trailingTimer = window.setTimeout(() => {
          trailingTimer = null;
          invalidate();
        }, remaining);
      }
    });

    return () => {
      unsubscribe();
      if (trailingTimer !== null) window.clearTimeout(trailingTimer);
    };
  }, [exact, qc, queryKeys, throttleMs]);
}
