import { QueryClient } from "@tanstack/react-query";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { mediaManagerKeys } from "../../../../lib/media-managers/query-keys";
import type {
  DiscoverableProcessingLibrary,
  ProcessingLibraryDrift,
} from "../../../../lib/processing/library-managers-api";
import { recordedResponse } from "../../../../test/recorded";
import { LibrariesTab } from "./libraries-tab";
import { library } from "./library-test-fixtures";
import {
  newTestClient,
  serveWorkflows,
  signedIn,
  workflowTabWrapper,
  type WorkflowServer,
} from "./workflow-server";

// The tab here is the real one, reading from a stand-in server at `fetch`. The file is about the Import section: asking a
// connected media manager which workflows it owns, importing some, and comparing what it reports with what Weir holds.
// The replies to a media manager that cannot be reached are recorded; no reply from a manager that answers was recorded,
// because the recording ran with none connected, so those are written from the shape the API client reads.

const DISCOVER = "GET /api/v1/processing/libraries/discover/1";
const DRIFT = "GET /api/v1/processing/libraries/discover/1/drift";
const IMPORT = "POST /api/v1/processing/libraries/discover/1/import";

const discovered = (
  over: Partial<DiscoverableProcessingLibrary>,
): DiscoverableProcessingLibrary => ({
  key: "movies-4k",
  name: "Movies 4K",
  media_type: "movie",
  root_path: "/manager/movies-4k",
  already_imported: false,
  local_path_problem: null,
  processes_before_import: true,
  output_path: "/manager/refined-4k",
  output_path_problem: null,
  ...over,
});

let server: WorkflowServer;
let client: QueryClient;

beforeEach(() => {
  client = newTestClient();
  serve("operator", [{ id: 1, kind: "deluno", name: "Deluno" }]);
});

afterEach(() => {
  cleanup();
  expect(server.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

function serve(
  role: "operator" | "viewer",
  connections: { id: number; kind: "deluno"; name: string }[],
) {
  server = serveWorkflows({
    workflows: [library()],
    role,
    connections,
  });
}

async function openTab() {
  render(<LibrariesTab />, { wrapper: workflowTabWrapper(client) });
  await screen.findByTestId("processing-library-1");
  await signedIn(client);
  await waitFor(() =>
    expect(client.getQueryState(mediaManagerKeys.connections)?.status).toBe(
      "success",
    ),
  );
}

async function askDeluno(button: "Discover workflows" | "Check for changes") {
  await openTab();
  fireEvent.change(await screen.findByLabelText("Media manager"), {
    target: { value: "1" },
  });
  fireEvent.click(screen.getByRole("button", { name: button }));
}

const notice = () => screen.findByTestId("processing-library-notice");

describe("Check for changes", () => {
  it("lists what differs, with the workflow, the detail, and what the manager and Weir each hold", async () => {
    const moved: ProcessingLibraryDrift = {
      kind: "root_moved",
      library_id: 1,
      library_name: "Movies",
      manager_value: "/media/films",
      weir_value: "/srv/movies/in",
      detail: "The media manager now keeps this workflow in another folder.",
    };
    server.set(DRIFT, [moved]);

    await askDeluno("Check for changes");

    const result = await screen.findByLabelText("Workflow comparison");
    expect(result).toHaveTextContent("Movies");
    expect(result).toHaveTextContent(moved.detail);
    expect(result).toHaveTextContent(
      "Manager: /media/films · Weir: /srv/movies/in",
    );
    expect(
      within(result).queryByText(
        "No manager/workflow path differences were found.",
      ),
    ).toBeNull();
  });

  it("says no differences were found when there are none", async () => {
    server.set(DRIFT, []);

    await askDeluno("Check for changes");

    expect(
      await screen.findByLabelText("Workflow comparison"),
    ).toHaveTextContent("No manager/workflow path differences were found.");
  });

  it("says why when the manager cannot be reached", async () => {
    server.set(DRIFT, recordedResponse(DRIFT));

    await askDeluno("Check for changes");

    expect(await notice()).toHaveTextContent(
      /Weir could not reach Deluno on localhost/,
    );
  });
});

describe("Discover workflows", () => {
  it("says why when the manager cannot be reached", async () => {
    server.set(DISCOVER, recordedResponse(DISCOVER));

    await askDeluno("Discover workflows");

    expect(await notice()).toHaveTextContent(
      /Weir could not reach Deluno on localhost/,
    );
    expect(screen.queryByLabelText("Discovered workflows")).toBeNull();
  });

  it("says so when the manager reports no workflows", async () => {
    server.set(DISCOVER, []);

    await askDeluno("Discover workflows");

    expect(
      await screen.findByText("That manager did not report any workflows."),
    ).toBeInTheDocument();
  });

  it("switches off, and says why, the workflows that cannot be imported", async () => {
    server.set(DISCOVER, [
      discovered({ key: "usable", name: "Documentaries" }),
      discovered({ key: "done", name: "Films", already_imported: true }),
      discovered({
        key: "unreachable",
        name: "Anime",
        local_path_problem:
          "Weir cannot see /manager/anime from this computer.",
      }),
      discovered({ key: "untyped", name: "Home videos", media_type: null }),
    ]);

    await askDeluno("Discover workflows");

    const list = await screen.findByLabelText("Discovered workflows");
    const box = (name: string) =>
      within(list).getByRole("checkbox", { name: new RegExp(name) });
    expect(box("Documentaries")).toBeEnabled();
    expect(box("Films")).toBeDisabled();
    expect(box("Anime")).toBeDisabled();
    expect(box("Home videos")).toBeDisabled();
    expect(list).toHaveTextContent("Already imported");
    expect(list).toHaveTextContent(
      "Weir cannot see /manager/anime from this computer.",
    );
    expect(list).toHaveTextContent(
      "The media manager did not say whether this is Movies or TV episodes, so Weir cannot import it.",
    );
  });
});

describe("Import selected", () => {
  it("says why when the import fails, and leaves the choice as it was", async () => {
    server.set(DISCOVER, [discovered({})]);
    server.set(IMPORT, recordedResponse(IMPORT));

    await askDeluno("Discover workflows");
    fireEvent.click(await screen.findByLabelText(/Movies 4K/));
    fireEvent.click(screen.getByRole("button", { name: "Import selected" }));

    expect(await notice()).toHaveTextContent(
      /Weir could not reach Deluno on localhost/,
    );
    expect(screen.getByLabelText(/Movies 4K/)).toBeChecked();
  });
});

describe("the Import section", () => {
  it("is not there for a viewer", async () => {
    serve("viewer", [{ id: 1, kind: "deluno", name: "Deluno" }]);
    await openTab();

    expect(
      screen.queryByRole("heading", { name: "Import from a media manager" }),
    ).toBeNull();
  });

  it("is not there when no media manager is connected", async () => {
    serve("operator", []);
    await openTab();

    expect(
      screen.queryByRole("heading", { name: "Import from a media manager" }),
    ).toBeNull();
  });

  it("is there for an operator with a media manager connected", async () => {
    await openTab();

    expect(
      await screen.findByRole("heading", {
        name: "Import from a media manager",
      }),
    ).toBeInTheDocument();
  });
});

describe("what the media manager leaves out", () => {
  it("says so for each path the manager did not report", async () => {
    server.set(DRIFT, [
      {
        kind: "library_added",
        library_id: null,
        library_name: "Anime",
        manager_value: null,
        weir_value: "/srv/anime",
        detail: "Weir holds a workflow the manager does not.",
      },
      {
        kind: "library_removed",
        library_id: null,
        library_name: "Cartoons",
        manager_value: "/media/cartoons",
        weir_value: null,
        detail: "The manager holds a workflow Weir does not.",
      },
      {
        kind: "path_not_local",
        library_id: 4,
        library_name: "Shorts",
        manager_value: null,
        weir_value: null,
        detail: "No paths on either side.",
      },
    ]);

    await askDeluno("Check for changes");

    const result = await screen.findByLabelText("Workflow comparison");
    expect(result).toHaveTextContent(
      "Manager: Not reported · Weir: /srv/anime",
    );
    expect(result).toHaveTextContent(
      "Manager: /media/cartoons · Weir: Not configured",
    );
    expect(result).not.toHaveTextContent("Not reported · Weir: Not");
  });

  it("shows no folders for a workflow the manager gave none, and lets a choice be taken back", async () => {
    server.set(DISCOVER, [discovered({ root_path: null, output_path: null })]);

    await askDeluno("Discover workflows");

    const list = await screen.findByLabelText("Discovered workflows");
    expect(list).toHaveTextContent("Manager path: Not supplied");
    expect(list).not.toHaveTextContent("Processed output");
    const box = within(list).getByRole("checkbox", { name: /Movies 4K/ });
    fireEvent.click(box);
    expect(box).toBeChecked();
    fireEvent.click(box);
    expect(box).not.toBeChecked();
    expect(
      screen.getByRole("button", { name: "Import selected" }),
    ).toBeDisabled();
  });
});
