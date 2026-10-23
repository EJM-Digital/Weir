/**
 * The lights on a connection's row, and the newest answer each one gave, kept from the `connection.activity` frames
 * the server pushes. There is one light per connection, so a burst of calls never stacks lights: the newest frame
 * replaces whatever the row was showing, and every light ends on its own timer, so none can stay on.
 */
import { useSyncExternalStore } from "react";

import { subscribeConnectionActivity } from "../activity/use-activity-stream-invalidation";
import { motionAllowed } from "../ui/motion-allowed";
import { parseAppTime } from "../ui/mm-format-date";
import type { ConnectionActivityFrame } from "./connection-activity";
import type { StatusMeaning } from "../ui/status-meaning";
import {
  CONNECTION_STATE_MEANING,
  connectionKey,
  type ConnectionAnswer,
  type ConnectionState,
} from "./connection-model";

/** `asking` is blue and blinking while a call is out; a call that ends flashes green or red. */
export type ConnectionLight = "asking" | "answered" | "failed";

export const CONNECTION_LIGHT_MEANING: Record<ConnectionLight, StatusMeaning> =
  {
    asking: "doing",
    answered: "done",
    failed: "broken",
  };

/** What a connection's row shows: the light while one is on, else what its state is. */
export function connectionRowMeaning(
  state: ConnectionState,
  light: ConnectionLight | null,
): StatusMeaning {
  return light
    ? CONNECTION_LIGHT_MEANING[light]
    : CONNECTION_STATE_MEANING[state];
}

/** How long a green or red flash stays. */
export const FLASH_MS = 1400;
/** A call that never says how it ended stops lighting its row after this long. */
export const ASKING_GIVES_UP_MS = 30_000;

type Snapshot = {
  lights: ReadonlyMap<string, ConnectionLight>;
  answers: ReadonlyMap<string, ConnectionAnswer>;
};

const NOTHING: Snapshot = { lights: new Map(), answers: new Map() };

let snapshot: Snapshot = NOTHING;
const listeners = new Set<() => void>();
const timers = new Map<string, ReturnType<typeof setTimeout>>();
let stopListening: (() => void) | null = null;

function publish(next: Partial<Snapshot>): void {
  snapshot = { ...snapshot, ...next };
  listeners.forEach((listener) => listener());
}

function lightsWith(
  key: string,
  light: ConnectionLight | null,
): ReadonlyMap<string, ConnectionLight> {
  const lights = new Map(snapshot.lights);
  if (light) lights.set(key, light);
  else lights.delete(key);
  return lights;
}

function stopTimer(key: string): void {
  const running = timers.get(key);
  if (running !== undefined) clearTimeout(running);
  timers.delete(key);
}

function putOut(key: string): void {
  stopTimer(key);
  if (snapshot.lights.has(key)) publish({ lights: lightsWith(key, null) });
}

function lightFor(key: string, light: ConnectionLight, lastsMs: number): void {
  stopTimer(key);
  publish({ lights: lightsWith(key, light) });
  timers.set(
    key,
    setTimeout(() => putOut(key), lastsMs),
  );
}

function answerOf(frame: ConnectionActivityFrame): ConnectionAnswer | null {
  const at = parseAppTime(frame.at);
  if (frame.phase === "asked" || at === null) return null;
  return {
    at,
    ms: frame.ms,
    ok: frame.direction === "inbound" ? null : frame.phase === "answered",
  };
}

/** Takes one stream frame: lights the connection's row, and remembers the answer a finished call gave. */
export function noteConnectionActivity(frame: ConnectionActivityFrame): void {
  const key = connectionKey(frame.kind, frame.id);
  const answer = answerOf(frame);
  if (answer) {
    const answers = new Map(snapshot.answers);
    answers.set(key, answer);
    publish({ answers });
  }
  if (!motionAllowed()) {
    putOut(key);
    return;
  }
  if (frame.phase === "asked") {
    lightFor(key, "asking", ASKING_GIVES_UP_MS);
  } else if (
    frame.direction === "outbound" ||
    snapshot.lights.get(key) !== "asking"
  ) {
    lightFor(key, frame.phase, FLASH_MS);
  }
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  stopListening ??= subscribeConnectionActivity(noteConnectionActivity);
  return () => {
    listeners.delete(listener);
    if (listeners.size > 0) return;
    stopListening?.();
    stopListening = null;
    timers.forEach(clearTimeout);
    timers.clear();
    snapshot = { ...snapshot, lights: NOTHING.lights };
  };
}

function getSnapshot(): Snapshot {
  return snapshot;
}

/** The light each connection's row shows now, by connection key, and the newest answer each one gave. */
export function useConnectionActivity(): Snapshot {
  return useSyncExternalStore(subscribe, getSnapshot);
}
