import { QueryClient } from "@tanstack/react-query";
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ProcessingLibrary } from "../../../../lib/processing/libraries-api";
import { processingKeys } from "../../../../lib/processing/query-keys";
import type { StubbedRequest } from "../../../../test/stub-weir-api";
import {
  elapse,
  useFakeClock,
  useRealClock,
} from "../../../../test/fake-clock";
import { FOLDER_CHECK_SETTLE_MS } from "./folder-check-settle";
import { LibrariesTab } from "./libraries-tab";
import { library } from "./library-test-fixtures";
import {
  folderChainRoute,
  managerSetupRoute,
  newTestClient,
  readyChain,
  serveWorkflows,
  signedIn,
  updateRoute,
  workflowTabWrapper,
  type WorkflowServer,
} from "./workflow-server";

let server: WorkflowServer;
let client: QueryClient;

const PROFILES = [
  { id: 1, name: "Archive" },
  { id: 2, name: "Strict English" },
];

beforeEach(() => {
  useFakeClock();
  client = newTestClient();
});

afterEach(() => {
  expect(server.unmatched).toEqual([]);
  useRealClock();
  vi.unstubAllGlobals();
});

async function openEditor(id = 1) {
  render(<LibrariesTab />, { wrapper: workflowTabWrapper(client) });
  const row = await screen.findByTestId(`processing-library-${id}`);
  await signedIn(client);
  fireEvent.click(within(row).getByRole("button", { name: "Edit" }));
  return screen.findByTestId("processing-library-form");
}

const field = (label: string) => screen.getByLabelText(label);
const save = () => screen.getByTestId("processing-library-save");

/** Saves and waits until the server has the change and the editor has closed. */
async function saveAndClose() {
  fireEvent.click(save());
  await waitFor(() =>
    expect(
      screen.queryByTestId("processing-library-form"),
    ).not.toBeInTheDocument(),
  );
}

const sentToServer = (id = 1) =>
  server.to(updateRoute(id))[0]!.body as Record<string, unknown>;

describe("the Rules profile of a workflow", () => {
  it("offers the default for its kind first, and sends the profile that was chosen", async () => {
    server = serveWorkflows({
      workflows: [library({ rule_set_id: null })],
      ruleSets: PROFILES,
    });
    await openEditor();

    const profile = field("Rules profile") as HTMLSelectElement;
    expect(
      within(profile)
        .getAllByRole("option")
        .map((option) => option.textContent),
    ).toEqual([
      "Movies default (the default for its kind)",
      "Archive",
      "Strict English",
    ]);
    expect(profile).toHaveValue("");

    fireEvent.change(profile, { target: { value: "2" } });
    await saveAndClose();

    expect(sentToServer().rule_set_id).toBe(2);
  });

  it("names the TV default for a TV workflow", async () => {
    server = serveWorkflows({
      workflows: [library({ media_type: "tv", rule_set_id: null })],
      ruleSets: PROFILES,
    });
    await openEditor();

    expect(
      within(field("Rules profile")).getByRole("option", {
        name: "TV default (the default for its kind)",
      }),
    ).toBeInTheDocument();
  });

  it("goes back to the default for its kind by sending none", async () => {
    server = serveWorkflows({
      workflows: [library({ rule_set_id: 2 })],
      ruleSets: PROFILES,
    });
    await openEditor();
    const profile = field("Rules profile");
    expect(profile).toHaveValue("2");

    // A person can only choose what the list offers.
    expect(
      within(profile).getByRole("option", {
        name: "Movies default (the default for its kind)",
      }),
    ).toBeInTheDocument();
    fireEvent.change(profile, { target: { value: "" } });
    await saveAndClose();

    expect(sentToServer().rule_set_id).toBeNull();
  });

  it("keeps a profile that was chosen through other edits", async () => {
    server = serveWorkflows({
      workflows: [library({ rule_set_id: 1 })],
      ruleSets: PROFILES,
    });
    await openEditor();

    fireEvent.change(field("Name"), { target: { value: "Films" } });
    await saveAndClose();

    expect(sentToServer()).toMatchObject({ name: "Films", rule_set_id: 1 });
  });
});

describe("the Media type of a workflow", () => {
  it("sends TV episodes when it is chosen, and the default profile follows the kind", async () => {
    server = serveWorkflows({
      workflows: [library({ media_type: "movie" })],
      ruleSets: PROFILES,
    });
    await openEditor();
    expect(field("Media type")).toHaveValue("movie");

    fireEvent.change(field("Media type"), { target: { value: "tv" } });
    expect(
      within(field("Rules profile")).getByRole("option", {
        name: "TV default (the default for its kind)",
      }),
    ).toBeInTheDocument();
    await saveAndClose();

    expect(sentToServer().media_type).toBe("tv");
    expect(server.workflows[0]!.media_type).toBe("tv");
  });

  it("asks the media manager and the folder chain again for the new type, once it has settled", async () => {
    server = serveWorkflows({
      workflows: [
        library({ media_type: "movie", manager_connection_ids: [7] }),
      ],
      connections: [{ id: 7, kind: "deluno", name: "Deluno" }],
      extra: {
        [managerSetupRoute]: (request: StubbedRequest) => ({
          media_type: request.query.get("media_type"),
          managers: [],
        }),
      },
    });
    await openEditor();
    await waitFor(() => expect(server.to(managerSetupRoute)).toHaveLength(1));
    expect(server.to(managerSetupRoute)[0]!.query.get("media_type")).toBe(
      "movie",
    );
    expect(server.to(folderChainRoute(1))).toHaveLength(1);

    fireEvent.change(field("Media type"), { target: { value: "tv" } });
    await elapse(FOLDER_CHECK_SETTLE_MS - 1);
    expect(server.to(managerSetupRoute)).toHaveLength(1);
    expect(server.to(folderChainRoute(1))).toHaveLength(1);

    await elapse(1);
    await waitFor(() => expect(server.to(managerSetupRoute)).toHaveLength(2));
    expect(server.to(managerSetupRoute)[1]!.query.get("media_type")).toBe("tv");
    await waitFor(() => expect(server.to(folderChainRoute(1))).toHaveLength(2));
  });
});

describe("the folder chain", () => {
  /** What the server says about the folders it holds now: the chain it checks is always the saved one. */
  const chainOfSavedFolders = () =>
    readyChain(
      1,
      `Weir can read the watched folder ${server.workflows[0]!.watched_folder}.`,
    );

  it("is asked for again once an edited folder has stayed unchanged for the settle time, and not before", async () => {
    server = serveWorkflows({
      workflows: [library()],
      chains: { 1: chainOfSavedFolders },
    });
    await openEditor();
    await waitFor(() => expect(server.to(folderChainRoute(1))).toHaveLength(1));

    fireEvent.change(field("Watched folder"), {
      target: { value: "/srv/movies/incoming" },
    });
    await elapse(FOLDER_CHECK_SETTLE_MS - 1);
    expect(server.to(folderChainRoute(1))).toHaveLength(1);

    fireEvent.change(field("Watched folder"), {
      target: { value: "/srv/movies/incoming2" },
    });
    await elapse(FOLDER_CHECK_SETTLE_MS - 1);
    expect(server.to(folderChainRoute(1))).toHaveLength(1);

    await elapse(1);
    await waitFor(() => expect(server.to(folderChainRoute(1))).toHaveLength(2));
  });

  it("is checked again after Save changed the saved folders, never shown from before", async () => {
    server = serveWorkflows({
      workflows: [library({ watched_folder: "/srv/movies/in" })],
      chains: { 1: chainOfSavedFolders },
    });
    await openEditor();
    expect(
      await screen.findByText(
        "Weir can read the watched folder /srv/movies/in.",
      ),
    ).toBeInTheDocument();

    fireEvent.change(field("Watched folder"), {
      target: { value: "/srv/movies/incoming" },
    });
    await elapse(FOLDER_CHECK_SETTLE_MS);
    await waitFor(() => expect(server.to(folderChainRoute(1))).toHaveLength(2));
    await saveAndClose();

    fireEvent.click(screen.getByRole("button", { name: "Edit" }));
    expect(
      await screen.findByText(
        "Weir can read the watched folder /srv/movies/incoming.",
      ),
    ).toBeInTheDocument();
    expect(
      screen.queryByText("Weir can read the watched folder /srv/movies/in."),
    ).not.toBeInTheDocument();
    expect(server.to(folderChainRoute(1)).length).toBeGreaterThanOrEqual(3);
  });
});

describe("a workflow that changes on the server while its editor is open", () => {
  const syncedFromDeluno: ProcessingLibrary = library({
    watched_folder: "/downloads/old",
    output_folder: "/refined/old",
    manager_connection_ids: [7],
    folders_synced_from_connection_id: 7,
  });

  /** What a media manager's sync or a second browser does: the server changes, and Weir is told the workflows changed. */
  async function serverChanges(fields: Partial<ProcessingLibrary>) {
    server.change(1, fields);
    await client.invalidateQueries({ queryKey: processingKeys.libraries });
  }

  it("shows the folders the media manager now reports, and Save sends them", async () => {
    server = serveWorkflows({
      workflows: [syncedFromDeluno],
      connections: [{ id: 7, kind: "deluno", name: "Deluno" }],
    });
    await openEditor();
    expect(field("Watched folder")).toHaveValue("/downloads/old");

    await serverChanges({
      watched_folder: "/downloads/new",
      output_folder: "/refined/new",
    });
    await waitFor(() =>
      expect(field("Watched folder")).toHaveValue("/downloads/new"),
    );
    expect(field("Output folder")).toHaveValue("/refined/new");

    fireEvent.change(field("Name"), { target: { value: "Films" } });
    await saveAndClose();

    expect(sentToServer()).toMatchObject({
      name: "Films",
      watched_folder: "/downloads/new",
      output_folder: "/refined/new",
    });
  });

  it("does not count what the server changed as an edit of the person's", async () => {
    server = serveWorkflows({
      workflows: [syncedFromDeluno],
      connections: [{ id: 7, kind: "deluno", name: "Deluno" }],
    });
    await openEditor();

    await serverChanges({ watched_folder: "/downloads/new" });
    await waitFor(() =>
      expect(field("Watched folder")).toHaveValue("/downloads/new"),
    );
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    expect(
      screen.queryByTestId("settings-unsaved-changes"),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByTestId("processing-library-form"),
    ).not.toBeInTheDocument();
  });

  it("keeps what the person typed, and follows the server for the rest", async () => {
    server = serveWorkflows({ workflows: [library({ max_attempts: 3 })] });
    await openEditor();
    fireEvent.change(field("Name"), { target: { value: "Films" } });

    await serverChanges({
      name: "Movies HD",
      output_folder: "/srv/movies/out2",
    });
    await waitFor(() =>
      expect(field("Output folder")).toHaveValue("/srv/movies/out2"),
    );
    expect(field("Name")).toHaveValue("Films");
    await saveAndClose();

    expect(sentToServer()).toMatchObject({
      name: "Films",
      output_folder: "/srv/movies/out2",
    });
  });

  it("follows the server again for a field the person put back to what was saved", async () => {
    server = serveWorkflows({ workflows: [library()] });
    await openEditor();
    fireEvent.change(field("Name"), { target: { value: "Films" } });
    fireEvent.change(field("Name"), { target: { value: "Movies" } });

    await serverChanges({ name: "Movies HD" });

    await waitFor(() => expect(field("Name")).toHaveValue("Movies HD"));
  });

  it("saves the switch as the server has it, not as it was when the editor opened", async () => {
    server = serveWorkflows({ workflows: [library({ enabled: true })] });
    await openEditor();

    await serverChanges({ enabled: false, priority: 3 });
    await waitFor(() =>
      expect(screen.getByTestId("processing-library-form")).toBeInTheDocument(),
    );
    fireEvent.change(field("Name"), { target: { value: "Films" } });
    await saveAndClose();

    expect(sentToServer()).toMatchObject({
      name: "Films",
      enabled: false,
      priority: 3,
    });
  });
});
