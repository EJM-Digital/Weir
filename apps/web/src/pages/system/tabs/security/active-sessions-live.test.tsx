import { cleanup, screen, waitFor } from "@testing-library/react";
import { act } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { useLiveSync } from "../../../../lib/live/use-live-sync";
import {
  serveRecordedReplies,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import { ActiveSessionsSection } from "./active-sessions-section";
import { oneSession, threeSessions, twoSessions } from "./security-replies";
import {
  APP_SETTINGS,
  renderSecurity,
} from "../../../../test/security-test-support";

const SESSIONS = "/api/v1/auth/sessions";

/** The one stream the page opens, with the part of the browser's EventSource that the page uses. */
class FakeEventSource {
  static instances: FakeEventSource[] = [];
  private listeners = new Map<
    string,
    Set<(ev: MessageEvent<string>) => void>
  >();

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }

  addEventListener(type: string, cb: (ev: MessageEvent<string>) => void): void {
    this.listeners.set(type, (this.listeners.get(type) ?? new Set()).add(cb));
  }

  close(): void {}

  emit(type: string, data = ""): void {
    this.listeners.get(type)?.forEach((cb) => cb({ data } as MessageEvent));
  }
}

function Page() {
  useLiveSync();
  return <ActiveSessionsSection enabled />;
}

const bodyRows = () => screen.getAllByRole("row").slice(1);
const streamSays = (topic: string) =>
  act(async () => {
    FakeEventSource.instances
      .at(-1)!
      .emit("data.changed", JSON.stringify({ topic }));
  });

let weir: RecordedWeir | undefined;

beforeEach(() => {
  vi.stubGlobal("EventSource", FakeEventSource);
});

afterEach(() => {
  cleanup();
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  FakeEventSource.instances = [];
  vi.unstubAllGlobals();
});

describe("Active sessions: live", () => {
  it("lists a browser that signs in, and drops one that signs out, as the server says so, with no click and no reload", async () => {
    weir = serveRecordedReplies({
      ...APP_SETTINGS,
      [`GET ${SESSIONS}`]: [oneSession, twoSessions, threeSessions, oneSession],
    });
    renderSecurity(<Page />);
    await waitFor(() => expect(bodyRows()).toHaveLength(1));

    await streamSays("sessions");
    await waitFor(() => expect(bodyRows()).toHaveLength(2));
    await streamSays("sessions");
    await waitFor(() => expect(bodyRows()).toHaveLength(3));
    await streamSays("sessions");
    await waitFor(() => expect(bodyRows()).toHaveLength(1));

    expect(weir.sent("GET", SESSIONS)).toHaveLength(4);
    expect(weir.requests.filter((r) => r.method !== "GET")).toEqual([]);
  });

  it("leaves the list alone when the stream says something else changed", async () => {
    weir = serveRecordedReplies({
      ...APP_SETTINGS,
      [`GET ${SESSIONS}`]: [oneSession, twoSessions],
    });
    renderSecurity(<Page />);
    await waitFor(() => expect(bodyRows()).toHaveLength(1));

    await streamSays("pause");

    expect(weir.sent("GET", SESSIONS)).toHaveLength(1);
    expect(bodyRows()).toHaveLength(1);
  });
});
