import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { writeFromProcessingRuleSet } from "../../../../lib/processing/rule-sets-api";
import { recordedResponse } from "../../../../test/recorded";
import {
  stubWeirApi,
  type StubbedApi,
  type StubbedRequest,
} from "../../../../test/stub-weir-api";
import { CSRF_TOKEN } from "../../../../test/wire";
import { RulesPreviewPanel } from "./rules-preview-panel";
import { STORED } from "./profiles-test-support";

// The panel here is the real one, reading from a stand-in server at `fetch` that answers with the recorded replies. The file is
// about which file it is asked to try the rules on: the workflow chosen, and the path a person gives or browses to.

const RULES = writeFromProcessingRuleSet(STORED);
const PREVIEW_OF_A_FILE_ANYWHERE = recordedResponse(
  "POST /api/v1/processing/libraries/1/preview (full path)",
);
const PREVIEW_IN_THE_WATCHED_FOLDER = recordedResponse(
  "POST /api/v1/processing/libraries/1/preview (in the watched folder)",
);

let api: StubbedApi;

/** The preview requests, by the workflow they were sent for. */
const previewsFor = (libraryId: number) =>
  api.to(`POST /api/v1/processing/libraries/${libraryId}/preview`);
const sent = (request: StubbedRequest) =>
  request.body as Record<string, unknown>;

beforeEach(() => {
  api = stubWeirApi({
    "GET /api/v1/auth/csrf": { csrf_token: CSRF_TOKEN },
    "GET /api/v1/processing/libraries": recordedResponse(
      "GET /api/v1/processing/libraries",
    ),
    "POST /api/v1/processing/libraries/1/preview": PREVIEW_OF_A_FILE_ANYWHERE,
    "POST /api/v1/processing/libraries/2/preview":
      PREVIEW_IN_THE_WATCHED_FOLDER,
    "GET /api/v1/system/directories": {
      current_path: null,
      parent_path: null,
      entries: [
        { name: "D:", path: "D:\\Media", kind: "root", description: null },
      ],
    },
  });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function openPanel(disabled = false) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  render(
    <QueryClientProvider client={client}>
      <RulesPreviewPanel rules={RULES} disabled={disabled} />
    </QueryClientProvider>,
  );
}

async function openTryOn() {
  openPanel();
  fireEvent.click(screen.getByRole("button", { name: "Try on a file" }));
  await screen.findByRole("option", { name: "TV" });
}

const workflowPicker = () =>
  screen.getByRole("combobox", { name: /^Workflow/ });
const preview = () =>
  screen.getByRole("button", { name: /^(Preview|Checking…)$/ });
const relativePath = () =>
  screen.getByLabelText("Path within the watched or output folder");

function chooseAnywhere() {
  fireEvent.change(screen.getByLabelText("Where is the file?"), {
    target: { value: "anywhere" },
  });
}

describe("the workflow the file is tried against", () => {
  it("starts on the first workflow, and the preview is sent for the one chosen instead", async () => {
    await openTryOn();
    const workflow = workflowPicker();
    await waitFor(() => expect(workflow).toHaveValue("1"));

    fireEvent.change(workflow, { target: { value: "2" } });
    fireEvent.change(relativePath(), { target: { value: "Show/Episode.mkv" } });
    fireEvent.click(preview());

    await waitFor(() => expect(previewsFor(2)).toHaveLength(1));
    expect(previewsFor(1)).toHaveLength(0);
    expect(sent(previewsFor(2)[0]!)).toMatchObject({
      relative_path: "Show/Episode.mkv",
    });
  });
});

describe("a file anywhere on this machine", () => {
  it.each([
    ["C:\\Media\\Movies", "Movie.mkv", "C:\\Media\\Movies\\Movie.mkv"],
    ["C:\\Media\\Movies\\", "\\Movie.mkv", "C:\\Media\\Movies\\Movie.mkv"],
    ["/media/movies/", "/Movie.mkv", "/media/movies/Movie.mkv"],
    ["  /media/movies  ", "  Movie.mkv  ", "/media/movies/Movie.mkv"],
  ])(
    "joins the folder %j and the file name %j as %j with one separator",
    async (folder, fileName, joined) => {
      await openTryOn();
      chooseAnywhere();

      fireEvent.change(screen.getByLabelText("Folder"), {
        target: { value: folder },
      });
      fireEvent.change(screen.getByLabelText("File name"), {
        target: { value: fileName },
      });
      fireEvent.click(preview());

      await waitFor(() => expect(previewsFor(1)).toHaveLength(1));
      const request = sent(previewsFor(1)[0]!);
      expect(request.absolute_path).toBe(joined);
      expect(request).not.toHaveProperty("relative_path");
    },
  );

  it("fills the Folder from the folder browser", async () => {
    await openTryOn();
    chooseAnywhere();

    fireEvent.click(screen.getByRole("button", { name: "Browse" }));
    const dialog = await screen.findByRole("dialog", {
      name: "Choose the file's folder",
    });
    fireEvent.click(
      await within(dialog).findByRole("button", { name: "Select" }),
    );

    expect(screen.getByLabelText("Folder")).toHaveValue("D:\\Media");
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});

describe("the Preview button", () => {
  it("is off until a path is given", async () => {
    await openTryOn();
    await waitFor(() => expect(workflowPicker()).toHaveValue("1"));
    expect(preview()).toBeDisabled();

    fireEvent.change(relativePath(), { target: { value: "Movie.mkv" } });
    expect(preview()).toBeEnabled();

    fireEvent.change(relativePath(), { target: { value: "   " } });
    expect(preview()).toBeDisabled();
  });

  it("is off in the anywhere mode until a folder or file name is given", async () => {
    await openTryOn();
    await waitFor(() => expect(workflowPicker()).toHaveValue("1"));
    chooseAnywhere();
    expect(preview()).toBeDisabled();

    fireEvent.change(screen.getByLabelText("File name"), {
      target: { value: "Movie.mkv" },
    });
    expect(preview()).toBeEnabled();
  });

  it("reads Checking… with a line saying what is happening while the answer is on its way", async () => {
    api.set(
      "POST /api/v1/processing/libraries/1/preview",
      () => new Promise<never>(() => undefined),
    );
    await openTryOn();
    await waitFor(() => expect(workflowPicker()).toHaveValue("1"));
    fireEvent.change(relativePath(), { target: { value: "Movie.mkv" } });

    fireEvent.click(preview());

    expect(
      await screen.findByRole("button", { name: "Checking…" }),
    ).toBeDisabled();
    expect(screen.getByRole("status")).toHaveTextContent(
      "Reading the file and running the rules…",
    );
  });
});

describe("the panel for a viewer", () => {
  it("cannot be opened", () => {
    openPanel(true);

    expect(
      screen.getByRole("button", { name: "Try on a file" }),
    ).toBeDisabled();
    expect(api.requests.filter((request) => request.method !== "GET")).toEqual(
      [],
    );
  });
});
