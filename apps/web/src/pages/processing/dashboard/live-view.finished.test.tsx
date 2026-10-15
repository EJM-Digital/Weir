import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import {
  passFinished,
  processingFile,
  workflowRow,
} from "../../../test/dashboard-fixtures";
import {
  liveViewRoutes,
  quietWorld,
  type LiveWorld,
} from "../../../test/live-view-server";
import { stubWeirApi, type StubbedApi } from "../../../test/stub-weir-api";
import { LiveView } from "./live-view";

const FILE_LOG = "GET /api/v1/processing/files/7/log";
const CLEANED = {
  id: 301,
  created_at: "2026-10-15T09:00:00+00:00",
  module: "processing",
  event_type: "library.file_cleaned",
  title: "Cleaned Old Classics/Casablanca (1942).mkv",
  library_id: 2,
  relative_path: "Old Classics/Casablanca (1942).mkv",
  detail: JSON.stringify({
    relative_path: "Old Classics/Casablanca (1942).mkv",
  }),
};

let api: StubbedApi;

function Address() {
  const { pathname, search } = useLocation();
  return <p data-testid="address">{`${pathname}${search}`}</p>;
}

function show(world: LiveWorld) {
  api = stubWeirApi({
    ...liveViewRoutes(world),
    [FILE_LOG]: {
      file_id: 7,
      relative_path: "Harbour Lights (2024)/harbour.lights.2024.mkv",
      retention_days: 90,
      entries: [],
    },
  });
  render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <MemoryRouter initialEntries={["/"]}>
        <LiveView
          filter="all"
          workflowId={null}
          layout={{ sideBySide: true, band: "across" }}
        />
        <Address />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const shelf = () => screen.findByRole("region", { name: "Just finished" });

afterEach(() => {
  expect(api.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

describe("opening a file from Just finished", () => {
  it("opens the file's story, reading what happened to it, when the file is in the loaded list", async () => {
    const world = quietWorld();
    world.files = [
      processingFile({
        id: 7,
        relative_path: "Harbour Lights (2024)/harbour.lights.2024.mkv",
        status: "processed",
      }),
    ];
    world.passes = [
      passFinished(201, "Harbour Lights (2024)/harbour.lights.2024.mkv"),
    ];
    show(world);

    fireEvent.click(
      await within(await shelf()).findByRole("button", {
        name: /harbour lights/i,
      }),
    );

    expect(await screen.findByRole("dialog")).toHaveTextContent(
      /harbour lights/i,
    );
    expect(screen.getByTestId("address")).toHaveTextContent(/^\/$/);
    expect(api.to(FILE_LOG)).toHaveLength(1);
  });

  it("goes to the file in Activity, with its path in the search, when it is older than the loaded list reaches", async () => {
    const world = quietWorld();
    world.passes = [
      passFinished(202, "Old Films (1999)/old.film.1999 & more.mkv"),
    ];
    show(world);

    fireEvent.click(
      await within(await shelf()).findByRole("button", { name: /old film/i }),
    );

    expect(screen.getByTestId("address")).toHaveTextContent(
      `/activity?q=${encodeURIComponent("Old Films (1999)/old.film.1999 & more.mkv")}`,
    );
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(api.to(FILE_LOG)).toHaveLength(0);
  });

  it("goes to the file in Library, with its path, when a library clean finished it", async () => {
    const world = quietWorld();
    world.workflows = [
      workflowRow({ id: 1, name: "Movies" }),
      workflowRow({ id: 2, name: "Old Classics" }),
    ];
    world.cleans = [CLEANED];
    show(world);

    fireEvent.click(
      await within(await shelf()).findByRole("button", {
        name: /casablanca/i,
      }),
    );

    expect(screen.getByTestId("address")).toHaveTextContent(
      `/library?path=${encodeURIComponent("Old Classics/Casablanca (1942).mkv")}`,
    );
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("does not take another workflow's file of the same name for the one that finished", async () => {
    const world = quietWorld();
    world.workflows = [
      workflowRow({ id: 1, name: "Movies" }),
      workflowRow({ id: 2, name: "Kids" }),
    ];
    world.files = [
      processingFile({
        id: 7,
        library_id: 1,
        relative_path: "Same Name (2020)/same.name.mkv",
        status: "processed",
      }),
    ];
    world.passes = [
      passFinished(203, "Same Name (2020)/same.name.mkv", { libraryId: 2 }),
    ];
    show(world);

    fireEvent.click(
      await within(await shelf()).findByRole("button", {
        name: /same name/i,
      }),
    );

    expect(screen.getByTestId("address")).toHaveTextContent("/activity?q=");
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});
