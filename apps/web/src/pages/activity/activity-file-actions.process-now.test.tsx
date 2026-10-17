import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { ProcessingFile } from "../../lib/processing/files-api";
import { processingFile, workflowRow } from "../../test/dashboard-fixtures";
import { WithWorkflows } from "../../test/with-workflows";
import {
  jsonAnswer,
  stubWeirApi,
  type StubbedApi,
} from "../../test/stub-weir-api";
import { ActivityFileActions } from "./activity-file-actions";

const ENQUEUE = "POST /api/v1/processing/jobs/file-remux-pass/enqueue";
const CSRF_TOKEN = "csrf-for-this-test";
const QUEUED =
  "Queued this file. It starts as soon as Weir finishes what it is working on.";

/** The workflows are already loaded, as they are on any page, so the scope of a file is known when it is clicked. */
const WORKFLOWS = [
  workflowRow({ id: 1, name: "Movies" }),
  workflowRow({ id: 2, name: "TV", media_type: "tv" }),
];

let api: StubbedApi;

function stubServer(routes: Record<string, unknown> = {}) {
  api = stubWeirApi({
    "GET /api/v1/media-managers/connections": [],
    "GET /api/v1/auth/csrf": { csrf_token: CSRF_TOKEN },
    [ENQUEUE]: {
      ok: true,
      job_id: 41,
      dedupe_key: "remux:Film (2024)/film.mkv",
      job_kind: "processing.file.remux_pass.v1",
    },
    ...routes,
  });
}

function unprocessed(partial: Partial<ProcessingFile> = {}): ProcessingFile {
  return processingFile({
    status: "unprocessed",
    status_reason: "Waiting for its turn.",
    ...partial,
  });
}

function renderActions(file: ProcessingFile = unprocessed()) {
  render(
    <WithWorkflows workflows={WORKFLOWS}>
      <ActivityFileActions file={file} editable onRemoved={vi.fn()} />
    </WithWorkflows>,
  );
}

const processNow = () => screen.getByRole("button", { name: "Process now" });

afterEach(() => {
  expect(api.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

describe("Process now on a file that has not been processed", () => {
  it("is offered only on a file that has not been processed", () => {
    stubServer();

    renderActions(unprocessed({ status: "processing_failed" }));

    expect(screen.queryByRole("button", { name: "Process now" })).toBeNull();
  });

  it("queues the file's path in its movie workflow, and says it is queued", async () => {
    stubServer();
    renderActions();

    fireEvent.click(processNow());

    expect(await screen.findByRole("status")).toHaveTextContent(QUEUED);
    expect(api.to(ENQUEUE)).toHaveLength(1);
    expect(api.to(ENQUEUE)[0].body).toEqual({
      relative_media_path: "Film (2024)/film.mkv",
      media_scope: "movie",
      library_id: 1,
      csrf_token: CSRF_TOKEN,
    });
  });

  it("queues a file of a TV workflow as TV", async () => {
    stubServer();
    renderActions(
      unprocessed({
        library_id: 2,
        relative_path: "Northbound/Northbound.S08E09.mkv",
      }),
    );

    fireEvent.click(processNow());

    await screen.findByRole("status");
    expect(api.to(ENQUEUE)[0].body).toMatchObject({
      relative_media_path: "Northbound/Northbound.S08E09.mkv",
      media_scope: "tv",
      library_id: 2,
    });
  });

  it("says Queueing… and holds every action while the server is answering, then lets them go", async () => {
    let answer: () => void = () => undefined;
    const answered = new Promise<void>((resolve) => (answer = resolve));
    stubServer({
      [ENQUEUE]: async () => {
        await answered;
        return { ok: true, job_id: 41, dedupe_key: "k", job_kind: "j" };
      },
    });
    renderActions();

    fireEvent.click(processNow());

    const queueing = await screen.findByRole("button", { name: "Queueing…" });
    expect(queueing).toBeDisabled();
    expect(screen.getByRole("button", { name: "Move to top" })).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "Remove from list" }),
    ).toBeDisabled();
    answer();
    expect(await screen.findByRole("status")).toHaveTextContent(QUEUED);
    expect(processNow()).toBeEnabled();
  });

  it("says why when the server refuses, in the server's own words, and can be tried again", async () => {
    stubServer({
      [ENQUEUE]: jsonAnswer(
        {
          detail:
            "The selected workflow no longer exists. Refresh Workflows and try again.",
        },
        404,
      ),
    });
    renderActions();

    fireEvent.click(processNow());

    expect(await screen.findByRole("status")).toHaveTextContent(
      "The selected workflow no longer exists. Refresh Workflows and try again.",
    );
    expect(screen.queryByText(QUEUED)).toBeNull();
    expect(processNow()).toBeEnabled();
  });

  it("says the file could not be queued when Weir cannot be reached", async () => {
    stubServer({
      [ENQUEUE]: () => {
        throw new TypeError("Failed to fetch");
      },
    });
    renderActions();

    fireEvent.click(processNow());

    expect(await screen.findByRole("status")).toHaveTextContent(
      "Can't reach Weir",
    );
    expect(screen.queryByText(QUEUED)).toBeNull();
  });
});
