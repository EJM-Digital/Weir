import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import {
  filesPage,
  processingFile,
  readyServer,
  workflowRow,
} from "../../../test/dashboard-fixtures";
import { stubWeirApi, type StubbedApi } from "../../../test/stub-weir-api";
import { NeedsPanel } from "./needs-panel";
import { NEEDS_PANEL_ID } from "./show-needs-panel";

/**
 * The Needs you panel worked from the keyboard, against a stubbed `fetch`: every control of a row is a native button or
 * link in reading order, and when an action takes the row away the focus stays in the panel instead of falling to the page.
 */

const FILES = "GET /api/v1/processing/files";

let api: StubbedApi;
let listed = [
  processingFile({ id: 7, relative_path: "Heat.1995.1080p.mkv" }),
  processingFile({ id: 8, relative_path: "Ronin.1998.1080p.mkv" }),
];

function serve() {
  api = stubWeirApi({
    [FILES]: () => filesPage(listed),
    "GET /api/v1/processing/libraries": [
      workflowRow({ id: 1, name: "Movies" }),
    ],
    "GET /api/v1/media-managers/connections": [],
    "GET /api/v1/system/readiness": readyServer(),
    "GET /api/v1/processing/jobs/inspection": { jobs: [] },
    "GET /api/v1/auth/csrf": { csrf_token: "token-1" },
    "POST /api/v1/processing/files/7/requeue": () => {
      listed = listed.filter((file) => file.id !== 7);
      return { detail: "Queued.", requeued: 1, skipped: 0 };
    },
  });
  render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <MemoryRouter>
        <NeedsPanel />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const rowOf = async (title: string) =>
  (await screen.findByText(title)).closest("li") as HTMLElement;

afterEach(() => {
  vi.unstubAllGlobals();
  listed = [
    processingFile({ id: 7, relative_path: "Heat.1995.1080p.mkv" }),
    processingFile({ id: 8, relative_path: "Ronin.1998.1080p.mkv" }),
  ];
});

describe("a Needs you row from the keyboard", () => {
  it("offers its actions as native buttons and links, in the order they read, none left out of the Tab order", async () => {
    serve();

    const row = await rowOf("Heat (1995)");

    const stops = [...row.querySelectorAll<HTMLElement>("button, a[href]")];
    expect(stops.map((stop) => stop.textContent)).toEqual([
      "Try again",
      "Remove…",
      "Open in Activity →",
    ]);
    for (const stop of stops) {
      expect(stop).not.toHaveAttribute("tabindex", "-1");
      expect(stop).not.toBeDisabled();
    }
    stops[0]!.focus();
    expect(stops[0]).toHaveFocus();
  });

  it("keeps the focus in the panel when Try again takes the row away", async () => {
    serve();
    const row = await rowOf("Heat (1995)");
    const tryAgain = within(row).getByRole("button", { name: "Try again" });
    tryAgain.focus();

    fireEvent.click(tryAgain);

    await waitFor(() =>
      expect(screen.queryByText("Heat (1995)")).not.toBeInTheDocument(),
    );
    expect(api.to("POST /api/v1/processing/files/7/requeue")).toHaveLength(1);
    expect(document.getElementById(NEEDS_PANEL_ID)).toHaveFocus();
    expect(screen.getByText("Ronin (1998)")).toBeVisible();
  });

  it("leaves the focus alone when a row goes away while it is somewhere else", async () => {
    serve();
    const row = await rowOf("Heat (1995)");
    const outside = document.createElement("button");
    document.body.append(outside);
    outside.focus();

    fireEvent.click(within(row).getByRole("button", { name: "Try again" }));

    await waitFor(() =>
      expect(screen.queryByText("Heat (1995)")).not.toBeInTheDocument(),
    );
    expect(outside).toHaveFocus();
    outside.remove();
  });
});
