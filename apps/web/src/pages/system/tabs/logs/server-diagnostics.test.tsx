import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { ServerMetrics } from "../../../../lib/settings/types";
import { systemReplies } from "../../../../test/recorded-system";
import {
  serveRecordedReplies,
  type RecordedReply,
} from "../../../../test/recorded-weir";
import { ServerDiagnostics } from "./server-diagnostics";

const METRICS = "GET /api/v1/suite/metrics";

const HEALTHY: ServerMetrics = {
  uptime_seconds: 2 * 86_400 + 4 * 3_600 + 30,
  total_requests: 1250,
  average_response_ms: 12.34,
  error_log_count: 3,
  busiest_routes: [],
  status_counts: { "2xx": 1200, "3xx": 10, "4xx": 0, "5xx": 0 },
};

const reply = (metrics: ServerMetrics): RecordedReply => ({
  status: 200,
  body: JSON.stringify(metrics),
});

function show() {
  return render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <ServerDiagnostics />
    </QueryClientProvider>,
  );
}

/** The cell under a counter's name. */
function counter(name: string): HTMLElement {
  return document.querySelector(`td[data-label="${name}"]`) as HTMLElement;
}

afterEach(() => vi.unstubAllGlobals());

describe("Server diagnostics", () => {
  it("is folded away, named, and shows every counter as Loading... until the server has answered", async () => {
    serveRecordedReplies({ [METRICS]: () => new Promise(() => undefined) });

    show();

    expect(await screen.findByText("Server diagnostics")).toBeVisible();
    expect(document.querySelector("details")).not.toHaveAttribute("open");
    expect(screen.getAllByText("Loading...")).toHaveLength(5);
  });

  it("shows how long Weir has run, the requests handled, the average response, the logged failures and no request issues", async () => {
    serveRecordedReplies({ [METRICS]: reply(HEALTHY) });

    show();

    await screen.findByText("2d 4h");
    expect(counter("Requests handled")).toHaveTextContent("1250");
    expect(counter("Average response")).toHaveTextContent("12.3 ms");
    expect(counter("Logged failures")).toHaveTextContent("3");
    expect(counter("Request issues")).toHaveTextContent("No request issues");
    expect(
      screen.getByText(
        "Successful 1200 - Redirected 10 - Rejected or not found 0 - Server failures 0",
      ),
    ).toBeInTheDocument();
  });

  it("counts requests that were rejected or not found as request issues", async () => {
    serveRecordedReplies({
      [METRICS]: reply({
        ...HEALTHY,
        status_counts: { "2xx": 5, "4xx": 2 },
      }),
    });

    show();

    await screen.findByText("2d 4h");
    expect(counter("Request issues")).toHaveTextContent("2 request issues");
  });

  it("names server failures before anything else", async () => {
    serveRecordedReplies({
      [METRICS]: reply({
        ...HEALTHY,
        status_counts: { "2xx": 5, "4xx": 2, "5xx": 1 },
      }),
    });

    show();

    await screen.findByText("2d 4h");
    expect(counter("Request issues")).toHaveTextContent("1 server failure");
  });

  it("says a Weir that has only just started has just started", async () => {
    serveRecordedReplies({
      [METRICS]: reply({ ...HEALTHY, uptime_seconds: 0 }),
    });

    show();

    expect(await screen.findByText("Just started")).toBeInTheDocument();
  });

  it("says the diagnostics could not be loaded, with no counters, when the server fails", async () => {
    serveRecordedReplies({ [METRICS]: systemReplies.settingsPutFails });

    show();

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Weir couldn't load server diagnostics.",
    );
    expect(screen.queryByText("Requests handled")).toBeNull();
  });
});
