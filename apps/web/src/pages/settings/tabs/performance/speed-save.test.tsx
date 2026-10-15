import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, expect, it, vi } from "vitest";

import { adminMe, csrfToken } from "../../../../test/recorded-admin-session";
import { serveRecordedReplies } from "../../../../test/recorded-weir";
import {
  filesAtOnceNothingWaiting,
  filesAtOnceOneWaiting,
  operatorSettings,
  operatorSettingsSavedAsFour,
} from "./performance-replies";
import { SpeedTab } from "./speed-tab";

const SETTINGS_PATH = "/api/v1/processing/operator-settings";
const SAVE_LABEL = "Save performance settings";

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

it("after Save, Save goes away, the new number stays pressed and the readout is read again", async () => {
  const weir = serveRecordedReplies({
    "GET /api/v1/auth/me": adminMe,
    "GET /api/v1/auth/csrf": csrfToken,
    [`GET ${SETTINGS_PATH}`]: operatorSettings,
    [`PUT ${SETTINGS_PATH}`]: operatorSettingsSavedAsFour,
    "GET /api/v1/processing/files-at-once": [
      filesAtOnceNothingWaiting,
      filesAtOnceOneWaiting,
    ],
  });
  render(<SpeedTab />, { wrapper });

  expect(
    await screen.findByRole("button", { name: "1", pressed: true }),
  ).toBeInTheDocument();
  expect(
    await screen.findByTestId("processing-files-at-once-readout"),
  ).toHaveTextContent("0 running now. Nothing is waiting.");
  expect(screen.queryByRole("button", { name: SAVE_LABEL })).toBeNull();

  fireEvent.click(screen.getByRole("button", { name: "4" }));
  fireEvent.click(screen.getByRole("button", { name: SAVE_LABEL }));

  await waitFor(() =>
    expect(screen.queryByRole("button", { name: SAVE_LABEL })).toBeNull(),
  );
  expect(screen.getByRole("button", { name: "4" })).toHaveAttribute(
    "aria-pressed",
    "true",
  );
  expect(screen.getByRole("button", { name: "1" })).toHaveAttribute(
    "aria-pressed",
    "false",
  );
  await waitFor(() =>
    expect(
      screen.getByTestId("processing-files-at-once-readout"),
    ).toHaveTextContent(
      "0 running now. 1 file is waiting because this Weir's workers are switched off.",
    ),
  );
  expect(weir.sent("PUT", SETTINGS_PATH).map((r) => r.body)).toEqual([
    {
      max_concurrent_files: 4,
      runner_capacity: 1,
      runner_cost_sd: 0,
      runner_cost_720p: 0,
      runner_cost_1080p: 1,
      runner_cost_4k: 1,
      runner_budget_enabled: false,
      csrf_token: JSON.parse(csrfToken.body).csrf_token,
    },
  ]);
  expect(weir.sent("GET", "/api/v1/processing/files-at-once")).toHaveLength(2);
  expect(weir.unrecorded).toEqual([]);
});
