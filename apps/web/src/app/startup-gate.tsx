import { type ReactNode, useEffect, useState } from "react";

import { StatusDot } from "../components/panels/status-dot";

/** What the public `/ready` answers, in full: it names no steps, so the gate cannot show any. */
type ReadyPayload = {
  ready: boolean;
  status: string;
};

type StartupState =
  | { kind: "starting"; message: string; detail: string; elapsedMs: number }
  | { kind: "ready" }
  | { kind: "failed"; message: string };

const READY_PATH = "/ready";
const POLL_MS = 1000;
const STARTUP_TIMEOUT_MS = 60_000;
/** The bar starts a fifth full and creeps towards nine tenths; only ready or failed completes it. */
const ESTIMATE_START_PERCENT = 20;
const ESTIMATE_CAP_PERCENT = 90;
const ESTIMATE_MS_PER_PERCENT = 750;

const WAITING_TO_START = "Waiting for Weir to start.";
const WAITING_TO_ANSWER = "Waiting for the Weir server to answer.";

async function fetchReadiness(signal: AbortSignal): Promise<ReadyPayload> {
  const response = await fetch(READY_PATH, {
    method: "GET",
    headers: { Accept: "application/json" },
    cache: "no-store",
    signal,
  });
  // A not-ready answer is a 503 with its own body, so the body is read whatever the status.
  return (await response.json()) as ReadyPayload;
}

export function StartupGate({ children }: { children: ReactNode }) {
  const [state, setState] = useState<StartupState>({
    kind: "starting",
    message: "Starting Weir...",
    detail: WAITING_TO_START,
    elapsedMs: 0,
  });

  useEffect(() => {
    let cancelled = false;
    const startedAt = Date.now();
    let timer: number | undefined;

    const poll = async () => {
      const controller = new AbortController();
      try {
        const payload = await fetchReadiness(controller.signal);
        if (cancelled) {
          return;
        }
        if (payload.ready) {
          setState({ kind: "ready" });
          return;
        }
        const elapsedMs = Date.now() - startedAt;
        if (elapsedMs >= STARTUP_TIMEOUT_MS) {
          setState({
            kind: "failed",
            message:
              "Weir did not become ready in time. Check that the Weir server is still running, then refresh.",
          });
          return;
        }
        setState({
          kind: "starting",
          message: "Starting Weir...",
          detail: WAITING_TO_START,
          elapsedMs,
        });
        timer = window.setTimeout(poll, POLL_MS);
      } catch {
        if (cancelled) {
          return;
        }
        const elapsedMs = Date.now() - startedAt;
        if (elapsedMs >= STARTUP_TIMEOUT_MS) {
          setState({
            kind: "failed",
            message:
              "Weir did not respond in time. Check that the Weir server is running, then refresh.",
          });
          return;
        }
        setState({
          kind: "starting",
          message: "Starting Weir...",
          detail: WAITING_TO_ANSWER,
          elapsedMs,
        });
        timer = window.setTimeout(poll, POLL_MS);
      }
    };

    void poll();
    return () => {
      cancelled = true;
      if (timer !== undefined) {
        window.clearTimeout(timer);
      }
    };
  }, []);

  if (state.kind === "ready") {
    return <>{children}</>;
  }

  return (
    <main className="mm-startup">
      <div className="mm-startup__inner">
        <p className="mm-startup__eyebrow">Weir</p>
        <h1 className="mm-startup__title">{state.message}</h1>
        <p className="mm-startup__lead">
          Preparing the local database, background workers, and schedules before
          opening the app.
        </p>
        <div className="mm-startup__card">
          {/* An estimate, not a measure: the server reports steps, not a percentage. */}
          <div
            className="mm-startup__track"
            role="progressbar"
            aria-label="Starting Weir"
          >
            <div
              className="mm-startup__fill"
              style={{
                width:
                  state.kind === "failed"
                    ? "100%"
                    : `${Math.min(ESTIMATE_CAP_PERCENT, ESTIMATE_START_PERCENT + state.elapsedMs / ESTIMATE_MS_PER_PERCENT)}%`,
              }}
            />
          </div>
          <ul className="mm-startup__steps space-y-3">
            <li className="mm-startup__step">
              <StatusDot
                className="mm-startup__dot"
                meaning={state.kind === "failed" ? "broken" : "doing"}
              />
              <span>
                <span className="mm-startup__step-name">server</span>
                <span className="mm-startup__step-detail">
                  {state.kind === "failed" ? WAITING_TO_START : state.detail}
                </span>
              </span>
            </li>
          </ul>
        </div>
        {state.kind === "failed" ? (
          <button
            type="button"
            className="mm-startup__retry"
            onClick={() => window.location.reload()}
          >
            Try again
          </button>
        ) : null}
      </div>
    </main>
  );
}
