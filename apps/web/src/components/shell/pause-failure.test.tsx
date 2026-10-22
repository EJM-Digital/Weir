import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, expect, it, vi } from "vitest";

import * as authQueries from "../../lib/auth/queries";
import * as pauseApi from "../../lib/pause/pause-api";
import type { PauseState } from "../../lib/pause/pause-api";
import { usePauseQuery } from "../../lib/pause/pause-queries";
import { PauseControl } from "./pause-control";

const running: PauseState = {
  paused: false,
  paused_until: null,
  scan_while_paused: true,
  reason: "",
  in_flight_policy:
    "Work already running finishes. Pausing stops Weir starting anything new.",
};

/** What the header's pill reads: the pause as the page holds it. */
function PausedReadout() {
  const pause = usePauseQuery();
  return (
    <span data-testid="readout">
      {pause.data?.paused ? "Paused" : "Running"}
    </span>
  );
}

function renderHeader() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(
    <>
      <PausedReadout />
      <PauseControl />
    </>,
    { wrapper },
  );
}

afterEach(() => {
  vi.restoreAllMocks();
});

it("never shows Paused when the server refused the pause, and says it could not", async () => {
  vi.spyOn(authQueries, "useMeQuery").mockReturnValue({
    data: { role: "operator" },
  } as ReturnType<typeof authQueries.useMeQuery>);
  vi.spyOn(pauseApi, "fetchPause").mockResolvedValue(running);
  vi.spyOn(pauseApi, "savePause").mockRejectedValue(
    new Error("Could not change whether processing is paused"),
  );

  renderHeader();
  fireEvent.click(await screen.findByTestId("pause-open"));
  fireEvent.click(screen.getByTestId("pause-for-indefinite"));

  expect(await screen.findByTestId("pause-alert")).toHaveTextContent(
    "Weir couldn't pause. Try again.",
  );
  expect(screen.getByTestId("readout")).toHaveTextContent("Running");
  expect(screen.queryByTestId("pause-resume")).not.toBeInTheDocument();
  expect(screen.getByTestId("pause-menu")).toBeInTheDocument();
});

it("shows Paused only from what the server answers once it has stored the pause", async () => {
  vi.spyOn(authQueries, "useMeQuery").mockReturnValue({
    data: { role: "operator" },
  } as ReturnType<typeof authQueries.useMeQuery>);
  vi.spyOn(pauseApi, "fetchPause").mockResolvedValue(running);
  vi.spyOn(pauseApi, "savePause").mockResolvedValue({
    ...running,
    paused: true,
    reason:
      "Processing is paused. Weir will start work again when you resume it.",
  });

  renderHeader();
  fireEvent.click(await screen.findByTestId("pause-open"));
  fireEvent.click(screen.getByTestId("pause-for-indefinite"));

  await waitFor(() =>
    expect(screen.getByTestId("readout")).toHaveTextContent("Paused"),
  );
  expect(screen.getByTestId("pause-resume")).toBeInTheDocument();
});

it("says Resuming, and is off, while the server is asked to resume", async () => {
  vi.spyOn(authQueries, "useMeQuery").mockReturnValue({
    data: { role: "operator" },
  } as ReturnType<typeof authQueries.useMeQuery>);
  vi.spyOn(pauseApi, "fetchPause").mockResolvedValue({
    ...running,
    paused: true,
  });
  vi.spyOn(pauseApi, "savePause").mockReturnValue(new Promise(() => undefined));

  renderHeader();
  fireEvent.click(await screen.findByTestId("pause-resume"));

  await waitFor(() =>
    expect(screen.getByTestId("pause-resume")).toHaveTextContent("Resuming…"),
  );
  expect(screen.getByTestId("pause-resume")).toBeDisabled();
});
