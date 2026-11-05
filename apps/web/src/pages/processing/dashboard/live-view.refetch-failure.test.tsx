import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import { processingKeys } from "../../../lib/processing/query-keys";
import { liveViewRoutes, quietWorld } from "../../../test/live-view-server";
import { stubWeirApi, unexpectedProblem } from "../../../test/stub-weir-api";
import { LiveView } from "./live-view";

const FILES = "GET /api/v1/processing/files";

afterEach(() => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
});

describe("the Live view when a read it already holds fails again", () => {
  it("keeps the Dashboard, and the very same cards, instead of an error page", async () => {
    vi.stubEnv("DEV", false);
    const api = stubWeirApi(liveViewRoutes(quietWorld()));
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={["/"]}>
          <LiveView
            filter="all"
            workflowId={null}
            layout={{ sideBySide: true, band: "across" }}
          />
        </MemoryRouter>
      </QueryClientProvider>,
    );
    const today = await screen.findByRole("region", { name: "Today" });
    const pipeline = screen.getByRole("region", { name: "Pipeline" });

    api.set(FILES, unexpectedProblem);
    await act(() =>
      client.invalidateQueries({ queryKey: processingKeys.files }),
    );

    await waitFor(() =>
      expect(
        client
          .getQueryCache()
          .findAll({ queryKey: processingKeys.files })
          .some((query) => query.state.status === "error"),
      ).toBe(true),
    );
    expect(
      screen.queryByRole("heading", { name: "Weir hit a problem" }),
    ).toBeNull();
    expect(screen.getByRole("region", { name: "Today" })).toBe(today);
    expect(screen.getByRole("region", { name: "Pipeline" })).toBe(pipeline);
  });
});
