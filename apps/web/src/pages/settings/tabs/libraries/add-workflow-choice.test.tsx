import { QueryClient } from "@tanstack/react-query";
import { fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { SuggestedLibrary } from "../../../../lib/processing/library-setup-api";
import { jsonAnswer } from "../../../../test/stub-weir-api";
import { LibrariesTab } from "./libraries-tab";
import { library } from "./library-test-fixtures";
import {
  LIBRARIES,
  newTestClient,
  serveWorkflows,
  signedIn,
  workflowTabWrapper,
  type WorkflowServer,
} from "./workflow-server";

// The tab here is the real one, reading from a stand-in server at `fetch`. The file is about the question Add workflow asks
// first, and what happens when the media manager is asked what folders it holds.

const SUGGESTIONS = "GET /api/v1/processing/library-suggestions";

const suggestion = (over: Partial<SuggestedLibrary>): SuggestedLibrary => ({
  library_id: null,
  name: "Films",
  media_type: "movie",
  watched_folder: "/downloads/films",
  output_folder: "/downloads/Weir Ready/Films",
  source_label: "Deluno",
  manager_connection_ids: [5],
  ...over,
});

let server: WorkflowServer;
let client: QueryClient;

beforeEach(() => {
  client = newTestClient();
  server = serveWorkflows({
    workflows: [library()],
    connections: [{ id: 5, kind: "deluno", name: "Deluno" }],
  });
});

afterEach(() => {
  expect(server.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

async function chooseFromAManager() {
  render(<LibrariesTab />, { wrapper: workflowTabWrapper(client) });
  fireEvent.click(await screen.findByTestId("processing-library-add"));
  fireEvent.click(
    await screen.findByRole("radio", { name: /From a media manager/ }),
  );
  await signedIn(client);
  return within(screen.getByTestId("add-workflow-choice"));
}

describe("a media manager that reports several workflows", () => {
  beforeEach(() => {
    server.set(SUGGESTIONS, {
      libraries: [
        suggestion({}),
        suggestion({
          name: "Shows",
          media_type: "tv",
          watched_folder: "/downloads/shows",
          output_folder: "/downloads/Weir Ready/Shows",
        }),
      ],
      notes: [],
    });
  });

  it("asks Which one? with Movies and TV episodes, and Continue starts the one picked", async () => {
    const choice = await chooseFromAManager();
    fireEvent.click(screen.getByTestId("add-workflow-continue"));

    const which = await choice.findByRole("combobox", { name: "Which one?" });
    expect(
      within(which)
        .getAllByRole("option")
        .map((option) => option.textContent),
    ).toEqual(["Movies", "TV episodes"]);
    expect(screen.queryByTestId("processing-library-form")).toBeNull();

    fireEvent.change(which, { target: { value: "tv" } });
    fireEvent.click(screen.getByTestId("add-workflow-continue"));

    const form = await screen.findByTestId("processing-library-form");
    expect(within(form).getByLabelText("Name")).toHaveValue("Shows");
    expect(within(form).getByLabelText("Watched folder")).toHaveValue(
      "/downloads/shows",
    );
    expect(server.to(`POST ${LIBRARIES}`)).toHaveLength(0);
  });
});

describe("asking the media manager for its folders", () => {
  it("says Asking… and holds Continue while the answer is on its way", async () => {
    server.set(SUGGESTIONS, () => new Promise<never>(() => undefined));
    const choice = await chooseFromAManager();

    fireEvent.click(screen.getByTestId("add-workflow-continue"));

    const asking = await choice.findByRole("button", { name: "Asking…" });
    expect(asking).toBeDisabled();
  });

  it("says why when it fails, and Continue works again", async () => {
    server.set(SUGGESTIONS, () =>
      jsonAnswer({ detail: "Deluno did not answer in time." }, 502),
    );
    const choice = await chooseFromAManager();

    fireEvent.click(screen.getByTestId("add-workflow-continue"));

    expect(await choice.findByRole("alert")).toHaveTextContent(
      "Deluno did not answer in time.",
    );
    expect(choice.getByRole("button", { name: "Continue" })).toBeEnabled();
    expect(screen.queryByTestId("processing-library-form")).toBeNull();

    server.set(SUGGESTIONS, { libraries: [], notes: [] });
    fireEvent.click(choice.getByRole("button", { name: "Continue" }));
    expect(await screen.findByTestId("processing-library-form")).toBeVisible();
  });
});

describe("Cancel on the choice", () => {
  it("closes it without opening an editor or saving anything", async () => {
    render(<LibrariesTab />, { wrapper: workflowTabWrapper(client) });
    fireEvent.click(await screen.findByTestId("processing-library-add"));
    expect(await screen.findByTestId("add-workflow-choice")).toBeVisible();

    fireEvent.click(
      within(screen.getByTestId("add-workflow-choice")).getByRole("button", {
        name: "Cancel",
      }),
    );

    expect(screen.queryByTestId("add-workflow-choice")).toBeNull();
    expect(screen.queryByTestId("processing-library-form")).toBeNull();
    expect(server.requests.filter((r) => r.method !== "GET")).toEqual([]);
  });
});
