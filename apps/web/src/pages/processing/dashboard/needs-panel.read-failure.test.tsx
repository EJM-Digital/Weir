import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import { NETWORK_UNREACHABLE_MESSAGE } from "../../../lib/api/api-error-text";
import {
  filesPage,
  processingFile,
  readyServer,
  workflowRow,
} from "../../../test/dashboard-fixtures";
import {
  jsonAnswer,
  stubWeirApi,
  type StubbedApi,
} from "../../../test/stub-weir-api";
import { NeedsPanel } from "./needs-panel";

const FILES = "GET /api/v1/processing/files";

let api: StubbedApi;

function stubServer(files: Response | unknown) {
  api = stubWeirApi({
    [FILES]: files,
    "GET /api/v1/processing/libraries": [
      workflowRow({ id: 1, name: "Movies" }),
    ],
    "GET /api/v1/media-managers/connections": [],
    "GET /api/v1/system/readiness": readyServer(),
    "GET /api/v1/processing/jobs/inspection": { jobs: [] },
  });
}

function renderPanel() {
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
  return screen.getByRole("region", { name: "Needs you" });
}

afterEach(() => {
  expect(api.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

describe("the Needs you panel when it cannot read the files", () => {
  it("says All clear only after the files were read and none need a person", async () => {
    stubServer(filesPage([]));

    const panel = renderPanel();

    expect(await within(panel).findByText("All clear")).toBeInTheDocument();
    expect(within(panel).queryByRole("alert")).toBeNull();
  });

  it("says it could not read the files, never All clear, when the server fails the read", async () => {
    stubServer(jsonAnswer({ detail: "The database is busy." }, 500));

    const panel = renderPanel();

    expect(await within(panel).findByRole("alert")).toHaveTextContent(
      "Weir couldn't load the files that need you.",
    );
    expect(within(panel).queryByText("All clear")).toBeNull();
    expect(within(panel).queryByText("Nothing needs you.")).toBeNull();
  });

  it("says Weir cannot be reached, never All clear, when the server does not answer", async () => {
    stubServer(filesPage([]));
    api.set(FILES, () => {
      throw new TypeError("Failed to fetch");
    });

    const panel = renderPanel();

    expect(await within(panel).findByRole("alert")).toHaveTextContent(
      NETWORK_UNREACHABLE_MESSAGE,
    );
    expect(within(panel).queryByText("All clear")).toBeNull();
  });

  it("still lists what Weir itself needs, beside the note, when only the files failed", async () => {
    stubServer(jsonAnswer({ detail: "The database is busy." }, 500));
    api.set("GET /api/v1/system/readiness", {
      ...readyServer(),
      worker_health: [
        {
          module: "processing",
          status: "degraded",
          detail: "The processing worker stopped.",
          expected_workers: 1,
          active_workers: 0,
          stale_workers: 0,
          stopped_workers: 1,
        },
      ],
    });

    const panel = renderPanel();

    expect(await within(panel).findByRole("alert")).toHaveTextContent(
      "Weir couldn't load the files that need you.",
    );
    await waitFor(() =>
      expect(within(panel).getByTestId("live-needs")).toBeInTheDocument(),
    );
    expect(within(panel).queryByText("All clear")).toBeNull();
  });

  it("lists the files of a read that worked, with no note", async () => {
    stubServer(
      filesPage([processingFile({ id: 9, relative_path: "Ember.mkv" })]),
    );

    const panel = renderPanel();

    expect(await within(panel).findByTestId("live-needs")).toHaveTextContent(
      "Ember",
    );
    expect(within(panel).queryByRole("alert")).toBeNull();
  });
});
