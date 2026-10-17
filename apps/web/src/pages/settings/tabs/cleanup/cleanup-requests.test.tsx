import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, within } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { adminMe, csrfToken } from "../../../../test/recorded-admin-session";
import type { MaintenanceState } from "../../../../lib/processing/maintenance-api";
import * as settingsQueries from "../../../../lib/settings/queries";
import {
  serveRecordedReplies,
  type RecordedReply,
  type RecordedRoutes,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import {
  saveRefusedTokenExpired,
  settingsDefault,
} from "../operator-settings-replies";
import { CleanupTab } from "./cleanup-tab";
import {
  maintenance,
  movieSweepQueued,
  runRefusedTokenExpired,
  tvSweepQueued,
} from "./cleanup-replies";

const OPERATOR_SETTINGS = "/api/v1/processing/operator-settings";
const RUN = "/api/v1/processing/maintenance/run";

const SAVE_FAILED = "That change could not be saved. Refresh and try again.";

const SWEEP = "processing-maintenance-work_temp_stale_sweep";
const HANDBACKS = "processing-maintenance-unclaimed_handbacks";

const [sweepState, handbackState] = (
  JSON.parse(maintenance.body) as MaintenanceState
).families;

const token = (JSON.parse(csrfToken.body) as { csrf_token: string }).csrf_token;

let weir: RecordedWeir | undefined;

beforeEach(() => {
  vi.spyOn(settingsQueries, "useAppSettingsQuery").mockReturnValue({
    data: undefined,
  } as ReturnType<typeof settingsQueries.useAppSettingsQuery>);
});

afterEach(() => {
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function serve(routes: RecordedRoutes = {}): RecordedWeir {
  weir = serveRecordedReplies({
    "GET /api/v1/auth/csrf": csrfToken,
    "GET /api/v1/auth/me": adminMe,
    "GET /api/v1/processing/maintenance": maintenance,
    "GET /api/v1/processing/operator-settings": settingsDefault,
    ...routes,
  });
  return weir;
}

async function sweepRow() {
  return screen.findByTestId(SWEEP);
}

async function handbackRow() {
  return screen.findByTestId(HANDBACKS);
}

describe("the destructive job's confirmation", () => {
  it("says what switching it on will delete, and that nothing deleted can be recovered, before anything is saved", async () => {
    const server = serve();

    render(<CleanupTab />, { wrapper });
    fireEvent.click(
      within(await handbackRow()).getByRole("radio", { name: "On" }),
    );

    const dialog = await screen.findByTestId(
      `processing-maintenance-confirm-${handbackState.family}-enable`,
    );
    expect(dialog).toHaveTextContent(
      `Switch on "Cleaned copies nobody picked up"?`,
    );
    expect(dialog).toHaveTextContent(handbackState.description);
    expect(dialog).toHaveTextContent("Files it deletes cannot be recovered.");
    expect(server.sent("PUT", OPERATOR_SETTINGS)).toEqual([]);
  });

  it("says the same before running it now, and runs nothing until it is confirmed", async () => {
    const server = serve();

    render(<CleanupTab />, { wrapper });
    await handbackRow();
    fireEvent.click(
      screen.getByTestId(`processing-maintenance-run-${handbackState.family}`),
    );

    const dialog = await screen.findByTestId(
      `processing-maintenance-confirm-${handbackState.family}-run`,
    );
    expect(dialog).toHaveTextContent(
      `Run "Cleaned copies nobody picked up" now?`,
    );
    expect(dialog).toHaveTextContent(handbackState.description);
    expect(dialog).toHaveTextContent("Files it deletes cannot be recovered.");
    expect(server.sent("POST", RUN)).toEqual([]);
  });
});

describe("Run now", () => {
  it("runs the job for movies, then for TV, and says it started", async () => {
    const server = serve({
      [`POST ${RUN}`]: [movieSweepQueued, tvSweepQueued],
    });

    render(<CleanupTab />, { wrapper });
    await sweepRow();
    fireEvent.click(
      screen.getByTestId(`processing-maintenance-run-${sweepState.family}`),
    );

    expect(
      await screen.findByText(
        "Leftover work files: started. Its result shows under Last run.",
      ),
    ).toBeInTheDocument();
    expect(server.sent("POST", RUN).map((request) => request.body)).toEqual([
      { family: sweepState.family, media_scope: "movie", csrf_token: token },
      { family: sweepState.family, media_scope: "tv", csrf_token: token },
    ]);
  });

  it("says the job could not be started, and does not try TV, when the movies run is refused", async () => {
    const server = serve({ [`POST ${RUN}`]: runRefusedTokenExpired });

    render(<CleanupTab />, { wrapper });
    await sweepRow();
    fireEvent.click(
      screen.getByTestId(`processing-maintenance-run-${sweepState.family}`),
    );

    expect(
      await screen.findByText("Leftover work files could not be started."),
    ).toBeInTheDocument();
    expect(server.sent("POST", RUN).map((request) => request.body)).toEqual([
      { family: sweepState.family, media_scope: "movie", csrf_token: token },
    ]);
  });

  it("says the job could not be started when only the TV run is refused", async () => {
    const server = serve({
      [`POST ${RUN}`]: [movieSweepQueued, runRefusedTokenExpired],
    });

    render(<CleanupTab />, { wrapper });
    await sweepRow();
    fireEvent.click(
      screen.getByTestId(`processing-maintenance-run-${sweepState.family}`),
    );

    expect(
      await screen.findByText("Leftover work files could not be started."),
    ).toBeInTheDocument();
    expect(server.sent("POST", RUN)).toHaveLength(2);
  });

  it("cannot be pressed again while a run is waiting for the server", async () => {
    let answer!: () => void;
    const held = new Promise<void>((resolve) => {
      answer = resolve;
    });
    const replies: RecordedReply[] = [movieSweepQueued, tvSweepQueued];
    let asked = 0;
    const server = serve({
      [`POST ${RUN}`]: async () => {
        await held;
        return replies[asked++];
      },
    });

    render(<CleanupTab />, { wrapper });
    await sweepRow();
    const runNow = screen.getByTestId(
      `processing-maintenance-run-${sweepState.family}`,
    );
    fireEvent.click(runNow);

    expect(runNow).toBeDisabled();
    fireEvent.click(runNow);
    answer();
    expect(
      await screen.findByText(
        "Leftover work files: started. Its result shows under Last run.",
      ),
    ).toBeInTheDocument();
    expect(runNow).toBeEnabled();
    expect(server.sent("POST", RUN)).toHaveLength(2);
  });
});

describe("a setting that could not be saved", () => {
  it("says so when a switch cannot be saved, and leaves the switch as it was saved", async () => {
    const server = serve({
      [`PUT ${OPERATOR_SETTINGS}`]: saveRefusedTokenExpired,
    });

    render(<CleanupTab />, { wrapper });
    fireEvent.click(
      within(await sweepRow()).getByRole("radio", { name: "Off" }),
    );

    expect(await screen.findByText(SAVE_FAILED)).toBeInTheDocument();
    expect(server.sent("PUT", OPERATOR_SETTINGS).map((r) => r.body)).toEqual([
      { work_temp_stale_sweep_enabled: false, csrf_token: token },
    ]);
    expect(
      within(await sweepRow()).getByRole("radio", { name: "On" }),
    ).toHaveAttribute("aria-checked", "true");
  });

  it("says so when a timer cannot be saved, and leaves the timer as it was saved", async () => {
    const server = serve({
      [`PUT ${OPERATOR_SETTINGS}`]: saveRefusedTokenExpired,
    });

    render(<CleanupTab />, { wrapper });
    const every = within(await sweepRow()).getByRole("combobox", {
      name: "How often Leftover work files runs",
    });
    fireEvent.change(every, { target: { value: "21600" } });

    expect(await screen.findByText(SAVE_FAILED)).toBeInTheDocument();
    expect(server.sent("PUT", OPERATOR_SETTINGS).map((r) => r.body)).toEqual([
      { work_temp_stale_sweep_interval_seconds: 21600, csrf_token: token },
    ]);
    expect(every).toHaveValue("3600");
  });

  it("says so when the days a copy waits cannot be saved, and keeps what was typed", async () => {
    const server = serve({
      [`PUT ${OPERATOR_SETTINGS}`]: saveRefusedTokenExpired,
    });

    render(<CleanupTab />, { wrapper });
    const days = await screen.findByLabelText(
      "Cleaned copies nobody picked up wait for",
    );
    fireEvent.change(days, { target: { value: "30" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByText(SAVE_FAILED)).toBeInTheDocument();
    expect(server.sent("PUT", OPERATOR_SETTINGS).map((r) => r.body)).toEqual([
      { unclaimed_handback_window_days: 30, csrf_token: token },
    ]);
    expect(days).toHaveValue(30);
    expect(screen.getByRole("button", { name: "Save" })).toBeEnabled();
  });
});
