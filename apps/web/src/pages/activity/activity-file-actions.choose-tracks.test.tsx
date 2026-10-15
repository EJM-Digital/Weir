import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { ProcessingFile } from "../../lib/processing/files-api";
import { processingFile, workflowRow } from "../../test/dashboard-fixtures";
import {
  jsonAnswer,
  stubWeirApi,
  type StubbedApi,
} from "../../test/stub-weir-api";
import { heldFileTracks } from "../../test/track-fixtures";
import { ActivityFileActions } from "./activity-file-actions";

const TRACKS = "GET /api/v1/processing/files/7/tracks";
const MANUAL_PLAN = "POST /api/v1/processing/files/7/manual-plan";
const CSRF_TOKEN = "csrf-for-this-test";
const REFUSED =
  "This file is not on hold, so a manual track choice does not apply. Refresh Activity and try again.";
const QUEUED =
  "Queued your track choice. Weir will check the file again before processing it.";

let api: StubbedApi;

function stubServer(routes: Record<string, unknown> = {}) {
  api = stubWeirApi({
    "GET /api/v1/processing/libraries": [
      workflowRow({ id: 1, name: "Movies" }),
    ],
    "GET /api/v1/media-managers/connections": [],
    "GET /api/v1/auth/csrf": { csrf_token: CSRF_TOKEN },
    [TRACKS]: heldFileTracks(),
    [MANUAL_PLAN]: {
      ok: true,
      job_id: 41,
      dedupe_key: "manual-plan:7",
      job_kind: "processing.file.remux_pass.v1",
    },
    ...routes,
  });
}

function renderActions(file: ProcessingFile = heldFile()) {
  render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <ActivityFileActions file={file} editable onRemoved={vi.fn()} />
    </QueryClientProvider>,
  );
}

function heldFile(partial: Partial<ProcessingFile> = {}): ProcessingFile {
  return processingFile({
    status: "on_hold",
    status_reason: "Held for your choice of tracks.",
    ...partial,
  });
}

const chooseTracks = () =>
  screen.getByRole("button", { name: "Choose tracks" });
const panel = () => screen.getByTestId("choose-tracks-panel");
const submit = () => screen.getByTestId("choose-tracks-submit");

async function openPanel() {
  fireEvent.click(chooseTracks());
  await screen.findByTestId("choose-tracks-submit");
}

afterEach(() => {
  expect(api.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

describe("Choose tracks on a held file in Activity", () => {
  it("is offered on a held file only", () => {
    stubServer();

    renderActions(heldFile({ status: "processing_failed" }));

    expect(screen.queryByRole("button", { name: "Choose tracks" })).toBeNull();
  });

  it("reads the file's tracks when it is clicked, says so while it reads, then opens the panel on them", async () => {
    let tracksArrive: () => void = () => undefined;
    const arrived = new Promise<void>((resolve) => (tracksArrive = resolve));
    stubServer({
      [TRACKS]: async () => {
        await arrived;
        return heldFileTracks();
      },
    });
    renderActions();
    expect(api.to(TRACKS)).toHaveLength(0);

    fireEvent.click(chooseTracks());

    expect(panel()).toHaveTextContent("Reading a fresh probe of this file…");
    await waitFor(() => expect(api.to(TRACKS)).toHaveLength(1));
    tracksArrive();
    await within(panel()).findByRole("table");
    expect(panel()).toHaveTextContent("Film (2024)/film.mkv");
    expect(screen.getByTestId("choose-tracks-keep-1")).toBeChecked();
    expect(screen.getByTestId("choose-tracks-keep-2")).not.toBeChecked();
  });

  it("queues the choice with the kept tracks and their order, then closes the panel and says it is queued", async () => {
    stubServer();
    renderActions();
    await openPanel();

    fireEvent.click(submit());

    await waitFor(() =>
      expect(screen.queryByTestId("choose-tracks-panel")).toBeNull(),
    );
    expect(screen.getByRole("status")).toHaveTextContent(QUEUED);
    expect(api.to(MANUAL_PLAN)).toHaveLength(1);
    expect(api.to(MANUAL_PLAN)[0].body).toEqual({
      keep: [
        { index: 0, default: false, forced: false },
        { index: 1, default: true, forced: false },
        { index: 3, default: false, forced: false },
      ],
      order: [0, 1, 3],
      csrf_token: CSRF_TOKEN,
    });
  });

  it("queues what was changed: another audio track kept, the order moved, a subtitle default and forced", async () => {
    stubServer();
    renderActions();
    await openPanel();

    fireEvent.click(screen.getByTestId("choose-tracks-keep-2"));
    fireEvent.click(screen.getByTestId("choose-tracks-move-up-3"));
    fireEvent.click(screen.getByTestId("choose-tracks-default-3"));
    fireEvent.click(screen.getByTestId("choose-tracks-forced-3"));
    fireEvent.click(submit());

    await waitFor(() => expect(api.to(MANUAL_PLAN)).toHaveLength(1));
    expect(api.to(MANUAL_PLAN)[0].body).toEqual({
      keep: [
        { index: 0, default: false, forced: false },
        { index: 3, default: true, forced: true },
        { index: 1, default: true, forced: false },
        { index: 2, default: false, forced: false },
      ],
      order: [0, 3, 1, 2],
      csrf_token: CSRF_TOKEN,
    });
  });

  it("asks for a fresh CSRF token for the choice, sent in the body", async () => {
    stubServer();
    renderActions();
    await openPanel();

    fireEvent.click(submit());

    await waitFor(() => expect(api.to(MANUAL_PLAN)).toHaveLength(1));
    expect(api.to("GET /api/v1/auth/csrf")).toHaveLength(1);
  });

  it("keeps the panel open and says why when the server refuses the choice, and queues it when tried again", async () => {
    stubServer({
      [MANUAL_PLAN]: jsonAnswer({ detail: REFUSED }, 400),
    });
    renderActions();
    await openPanel();

    fireEvent.click(submit());

    expect(
      await screen.findByTestId("choose-tracks-submit-error"),
    ).toHaveTextContent(REFUSED);
    expect(screen.getByTestId("choose-tracks-panel")).toBeInTheDocument();
    expect(screen.queryByText(QUEUED)).toBeNull();

    api.set(MANUAL_PLAN, {
      ok: true,
      job_id: 42,
      dedupe_key: "k",
      job_kind: "j",
    });
    fireEvent.click(submit());

    await waitFor(() =>
      expect(screen.queryByTestId("choose-tracks-panel")).toBeNull(),
    );
    expect(screen.getByRole("status")).toHaveTextContent(QUEUED);
    expect(screen.queryByTestId("choose-tracks-submit-error")).toBeNull();
  });

  it("says what to do when Weir cannot be reached to queue the choice", async () => {
    stubServer({
      [MANUAL_PLAN]: () => {
        throw new TypeError("Failed to fetch");
      },
    });
    renderActions();
    await openPanel();

    fireEvent.click(submit());

    expect(
      await screen.findByTestId("choose-tracks-submit-error"),
    ).toHaveTextContent("Can't reach Weir");
  });

  it("shows the reason in the panel, with no table, when the tracks cannot be read", async () => {
    stubServer({
      [TRACKS]: jsonAnswer(
        { detail: "Weir could not read this file safely." },
        400,
      ),
    });
    renderActions();

    fireEvent.click(chooseTracks());

    expect(await within(panel()).findByRole("alert")).toHaveTextContent(
      "Weir could not read this file safely.",
    );
    expect(within(panel()).queryByRole("table")).toBeNull();
    expect(api.to(MANUAL_PLAN)).toHaveLength(0);
  });

  it("reads the tracks afresh each time it is opened", async () => {
    stubServer();
    renderActions();
    await openPanel();

    fireEvent.click(within(panel()).getByRole("button", { name: "Cancel" }));
    expect(screen.queryByTestId("choose-tracks-panel")).toBeNull();
    await openPanel();

    expect(api.to(TRACKS)).toHaveLength(2);
  });

  it("queues nothing when the panel is cancelled", async () => {
    stubServer();
    renderActions();
    await openPanel();

    fireEvent.click(within(panel()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByTestId("choose-tracks-panel")).toBeNull();
    expect(api.to(MANUAL_PLAN)).toHaveLength(0);
    expect(screen.queryByRole("status")).toBeNull();
  });
});
