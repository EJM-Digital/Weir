import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { StartupGate } from "./startup-gate";

describe("StartupGate", () => {
  beforeEach(() => {
    vi.useRealTimers();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it("shows startup state until backend readiness is confirmed", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => ({
        ok: false,
        json: async () => ({
          ready: false,
          status: "starting",
          startup_seconds: 0.1,
          steps: [
            {
              name: "database",
              status: "starting",
              detail: "Preparing database.",
            },
          ],
        }),
      })),
    );

    render(
      <StartupGate>
        <div>App mounted</div>
      </StartupGate>,
    );

    expect(await screen.findByText("Starting Weir...")).toBeInTheDocument();
    expect(screen.queryByText("App mounted")).not.toBeInTheDocument();
    expect(await screen.findByText("Preparing database.")).toBeInTheDocument();
  });

  it("mounts children after backend readiness is confirmed", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => ({
        ok: true,
        json: async () => ({
          ready: true,
          status: "ready",
          startup_seconds: 0.2,
          steps: [
            { name: "database", status: "ready", detail: "Database ready." },
            { name: "workers", status: "ready", detail: "Workers ready." },
          ],
        }),
      })),
    );

    render(
      <StartupGate>
        <div>App mounted</div>
      </StartupGate>,
    );

    await waitFor(() =>
      expect(screen.getByText("App mounted")).toBeInTheDocument(),
    );
    expect(screen.queryByText("Starting Weir...")).not.toBeInTheDocument();
  });
});

/** What the server answers on /ready, as the contract tests judge it (SuiteEndpointsContractTests). */
const READY = { ready: true, status: "ready" };
const STARTING = { ready: false, status: "starting" };

function readyReply(body: object, status: number): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

const answersReady = () => readyReply(READY, 200);
const answersStarting = () => readyReply(STARTING, 503);

const POLL_MS = 1000;
const GIVE_UP_MS = 60_000;

/** Lets each queued answer arrive in order; once they run out, every further call gets the last. */
function serverAnswering(...answers: Array<() => Response | Error>) {
  let next = 0;
  const fetchStub = vi.fn(async () => {
    const answer = answers[Math.min(next, answers.length - 1)];
    next += 1;
    const reply = answer();
    if (reply instanceof Error) throw reply;
    return reply;
  });
  vi.stubGlobal("fetch", fetchStub);
  return fetchStub;
}

/** Moves the clock on, inside act so what the page shows has been drawn when it returns. */
async function elapse(ms: number) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

/** A second at a time, so each answer is read before the next poll is due. */
async function passSeconds(seconds: number) {
  for (let second = 0; second < seconds; second += 1) {
    await elapse(POLL_MS);
  }
}

const unreachable = () => new TypeError("Failed to fetch");

/** Renders the gate and lets its first question to /ready be answered before any time passes. */
async function renderGate() {
  const rendered = render(
    <StartupGate>
      <div>App mounted</div>
    </StartupGate>,
  );
  await elapse(0);
  return rendered;
}

describe("StartupGate while Weir is starting", () => {
  const reload = vi.fn();

  beforeEach(() => {
    vi.useFakeTimers();
    vi.stubGlobal("location", { ...window.location, reload });
  });

  afterEach(() => {
    reload.mockReset();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("asks /ready again each second until it answers ready, then mounts the app and stops asking", async () => {
    const fetchStub = serverAnswering(
      answersStarting,
      answersStarting,
      answersReady,
    );

    await renderGate();
    expect(fetchStub).toHaveBeenCalledTimes(1);
    expect(fetchStub).toHaveBeenCalledWith(
      "/ready",
      expect.objectContaining({ method: "GET" }),
    );
    expect(screen.getByText("Starting Weir...")).toBeInTheDocument();

    await elapse(POLL_MS - 1);
    expect(fetchStub).toHaveBeenCalledTimes(1);

    await elapse(1);
    expect(fetchStub).toHaveBeenCalledTimes(2);
    expect(screen.queryByText("App mounted")).not.toBeInTheDocument();

    await elapse(POLL_MS);
    expect(fetchStub).toHaveBeenCalledTimes(3);
    expect(screen.getByText("App mounted")).toBeInTheDocument();
    expect(screen.queryByText("Starting Weir...")).not.toBeInTheDocument();

    await elapse(10 * POLL_MS);
    expect(fetchStub).toHaveBeenCalledTimes(3);
  });

  it("says it is waiting for the server when nothing answers, and keeps asking until it does", async () => {
    const fetchStub = serverAnswering(unreachable, unreachable, answersReady);

    await renderGate();
    expect(
      screen.getByText("Waiting for the Weir server to answer."),
    ).toBeInTheDocument();
    expect(screen.getByText("Starting Weir...")).toBeInTheDocument();

    await elapse(POLL_MS);
    expect(fetchStub).toHaveBeenCalledTimes(2);
    expect(
      screen.getByText("Waiting for the Weir server to answer."),
    ).toBeInTheDocument();

    await elapse(POLL_MS);
    expect(fetchStub).toHaveBeenCalledTimes(3);
    expect(screen.getByText("App mounted")).toBeInTheDocument();
  });

  it("gives up after a minute of not being ready, shows every step as broken, and Try again reloads the page", async () => {
    const fetchStub = serverAnswering(answersStarting);

    await renderGate();
    await passSeconds(GIVE_UP_MS / POLL_MS - 1);
    expect(screen.getByText("Starting Weir...")).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Try again" }),
    ).not.toBeInTheDocument();

    await elapse(POLL_MS);

    expect(
      screen.getByText(
        "Weir did not become ready in time. Check that the Weir server is still running, then refresh.",
      ),
    ).toBeInTheDocument();
    expect(screen.getByRole("progressbar").firstElementChild).toHaveStyle({
      width: "100%",
    });
    expect(
      document.querySelector(".mm-startup__step .mm-status-dot"),
    ).toHaveAttribute("data-status", "broken");
    const callsAtGivingUp = fetchStub.mock.calls.length;
    await elapse(10 * POLL_MS);
    expect(fetchStub).toHaveBeenCalledTimes(callsAtGivingUp);

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it("gives up after a minute of no answer at all, with its own words, and Try again reloads the page", async () => {
    serverAnswering(unreachable);

    await renderGate();
    await passSeconds(GIVE_UP_MS / POLL_MS);

    expect(
      screen.getByText(
        "Weir did not respond in time. Check that the Weir server is running, then refresh.",
      ),
    ).toBeInTheDocument();
    expect(screen.getByRole("progressbar").firstElementChild).toHaveStyle({
      width: "100%",
    });

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it("stops asking when the page goes away before Weir is ready", async () => {
    const fetchStub = serverAnswering(answersStarting);

    const { unmount } = await renderGate();
    await elapse(POLL_MS);
    const callsBeforeLeaving = fetchStub.mock.calls.length;
    unmount();
    await elapse(5 * POLL_MS);

    expect(fetchStub).toHaveBeenCalledTimes(callsBeforeLeaving);
  });
});
