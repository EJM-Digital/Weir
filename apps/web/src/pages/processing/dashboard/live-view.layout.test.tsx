import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import { stubActivityStream } from "../../../test/fake-activity-stream";
import { liveViewRoutes, quietWorld } from "../../../test/live-view-server";
import { stubWeirApi, type StubbedApi } from "../../../test/stub-weir-api";
import type { PageLayout } from "./dashboard-layout";
import { LiveView } from "./live-view";

let api: StubbedApi;

async function show(layout: PageLayout) {
  api = stubWeirApi(liveViewRoutes(quietWorld()));
  stubActivityStream();
  const view = render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <MemoryRouter>
        <LiveView filter="all" workflowId={null} layout={layout} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  await screen.findByTestId("live-done-today");
  return view.container;
}

afterEach(() => {
  expect(api.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

describe("the Dashboard laid out for the room it has", () => {
  it("puts the tiles side by side and lets the board and the shelf use the width when there is room", async () => {
    const page = await show({ sideBySide: true, band: "across" });

    expect(page.querySelector(".mm-dash__tiles--across")).not.toBeNull();
    expect(page.querySelector(".mm-dash__tiles--stacked")).toBeNull();
    expect(
      page.querySelector<HTMLElement>(".mm-dash__board")?.style.maxHeight,
    ).toBe("");
    expect(
      page.querySelector<HTMLElement>(".mm-dash__low")?.style
        .gridTemplateColumns,
    ).not.toBe("");
  });

  it("stacks the tiles and keeps the board to a share of the window height when the room is narrow", async () => {
    const page = await show({ sideBySide: false, band: "stacked" });

    expect(page.querySelector(".mm-dash__tiles--stacked")).not.toBeNull();
    expect(page.querySelector(".mm-dash__tiles--across")).toBeNull();
    expect(
      page.querySelector<HTMLElement>(".mm-dash__board")?.style.maxHeight,
    ).not.toBe("");
    expect(
      page.querySelector<HTMLElement>(".mm-dash__low")?.style
        .gridTemplateColumns,
    ).toBe("");
  });
});
