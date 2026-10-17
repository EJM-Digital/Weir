import { QueryClient } from "@tanstack/react-query";
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ProcessingLibrary } from "../../../../lib/processing/libraries-api";
import { jsonAnswer } from "../../../../test/stub-weir-api";
import { LibrariesTab } from "./libraries-tab";
import { library } from "./library-test-fixtures";
import {
  CSRF_TOKEN,
  newTestClient,
  serveWorkflows,
  signedIn,
  unlinkRoute,
  updateRoute,
  workflowTabWrapper,
  type WorkflowServer,
} from "./workflow-server";

let server: WorkflowServer;
let client: QueryClient;

beforeEach(() => {
  client = newTestClient();
});

afterEach(() => {
  expect(server.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

async function openTab() {
  render(<LibrariesTab />, { wrapper: workflowTabWrapper(client) });
  const row = await screen.findByTestId("processing-library-1");
  await signedIn(client);
  return row;
}

/** Every setting the editor's own Save also sends, set to something other than the default so a lost one shows. */
const configured: ProcessingLibrary = library({
  watched_folder: "/srv/movies/in",
  work_folder: "/srv/movies/work",
  output_folder: "/srv/movies/out",
  media_extensions_csv: ".mkv",
  exclude_markers_csv: "__sample__",
  include_patterns_csv: "*film*",
  exclude_patterns_csv: "*trailer*",
  min_file_size_mb: 120,
  max_file_size_mb: 90_000,
  rejected_file_action: "delete_file",
  ready_after_seconds: 90,
  created_after: "2026-01-01T00:00:00.000Z",
  exclude_hidden: false,
  top_level_only: true,
  scan_interval_seconds: 900,
  sidecar_patterns_csv: ".srt",
  preserve_original_timestamps: true,
  remove_original_after_success: false,
  minimum_free_disk_space_mb: 20_480,
  remux_writer: "ffmpeg",
  output_collision_policy: "skip",
  ffmpeg_strictness: "strict",
  ignore_size_changes: true,
  skip_access_tests: true,
  file_system_events_enabled: false,
  max_attempts: 5,
  retry_backoff_seconds: 600,
  retry_execution_failures: false,
  retry_preflight_failures: true,
  failure_policy: "hold",
  schedule_grid: "1".repeat(168),
  schedule_enabled: false,
  schedule_hours_limited: true,
  schedule_days: "mon,tue",
  schedule_start: "08:00",
  schedule_end: "20:00",
  max_concurrent_files: 2,
  priority: 4,
  rule_set_id: 2,
  manager_connection_ids: [5],
});

describe("the On / Off switch of a workflow", () => {
  it("saves at once with every other setting kept, and the list shows it off", async () => {
    server = serveWorkflows({ workflows: [configured] });
    const row = await openTab();
    const group = within(row).getByRole("radiogroup", {
      name: "Movies enabled",
    });
    expect(within(group).getByRole("radio", { name: "On" })).toBeChecked();

    fireEvent.click(within(group).getByRole("radio", { name: "Off" }));

    await waitFor(() => expect(server.to(updateRoute(1))).toHaveLength(1));
    const { csrf_token, enabled, ...rest } = server.to(updateRoute(1))[0]!
      .body as Record<string, unknown>;
    expect(csrf_token).toBe(CSRF_TOKEN);
    expect(enabled).toBe(false);
    const saved = configured as unknown as Record<string, unknown>;
    expect(Object.keys(rest).length).toBeGreaterThanOrEqual(45);
    for (const [field, value] of Object.entries(rest)) {
      expect({ field, value }).toEqual({ field, value: saved[field] });
    }
    await waitFor(() =>
      expect(within(group).getByRole("radio", { name: "Off" })).toBeChecked(),
    );
  });

  it("switches a workflow that is off back on, again keeping the rest", async () => {
    server = serveWorkflows({ workflows: [{ ...configured, enabled: false }] });
    const row = await openTab();
    const group = within(row).getByRole("radiogroup", {
      name: "Movies enabled",
    });

    fireEvent.click(within(group).getByRole("radio", { name: "On" }));

    await waitFor(() => expect(server.to(updateRoute(1))).toHaveLength(1));
    expect(server.to(updateRoute(1))[0]!.body).toMatchObject({
      enabled: true,
      remux_writer: "ffmpeg",
      rule_set_id: 2,
      priority: 4,
    });
  });

  it("sends nothing for a click on the side already chosen, only for the click after it on the other side", async () => {
    server = serveWorkflows({ workflows: [configured] });
    const row = await openTab();
    const group = within(row).getByRole("radiogroup", {
      name: "Movies enabled",
    });

    // Everything the page sends goes through one queue, so the other side clicked after it is the control: when its save has arrived,
    // anything the first click began would have arrived before it.
    fireEvent.click(within(group).getByRole("radio", { name: "On" }));
    fireEvent.click(within(group).getByRole("radio", { name: "Off" }));

    await waitFor(() => expect(server.to(updateRoute(1))).toHaveLength(1));
    expect(server.to(updateRoute(1))[0]!.body).toMatchObject({
      enabled: false,
    });
    expect(server.to("GET /api/v1/auth/csrf")).toHaveLength(1);
  });

  it("says why when the server refuses, and leaves the switch where the server has it", async () => {
    server = serveWorkflows({
      workflows: [configured],
      extra: {
        [updateRoute(1)]: () =>
          jsonAnswer(
            { detail: "Movies is being changed by another session." },
            409,
          ),
      },
    });
    const row = await openTab();
    const group = within(row).getByRole("radiogroup", {
      name: "Movies enabled",
    });

    fireEvent.click(within(group).getByRole("radio", { name: "Off" }));

    expect(
      await screen.findByTestId("processing-library-notice"),
    ).toHaveTextContent("Movies is being changed by another session.");
    expect(within(group).getByRole("radio", { name: "On" })).toBeChecked();
  });

  it("is not offered to a viewer", async () => {
    server = serveWorkflows({ workflows: [configured], role: "viewer" });
    const row = await openTab();

    await waitFor(() =>
      expect(within(row).getByRole("radio", { name: "Off" })).toBeDisabled(),
    );
    expect(within(row).getByRole("radio", { name: "On" })).toBeDisabled();
  });
});

describe("Unlink on a workflow imported from a media manager", () => {
  const imported = library({
    id: 1,
    name: "Movies 4K",
    discovered_from_connection_id: 7,
    discovered_library_key: "movies-4k",
    manager_connection_ids: [7],
  });

  it("unlinks it, says it is now Weir-only, and takes the button away", async () => {
    server = serveWorkflows({
      workflows: [imported, library({ id: 2, name: "TV", media_type: "tv" })],
      connections: [{ id: 7, kind: "deluno", name: "Deluno" }],
    });
    const row = await openTab();
    expect(within(row).getByText("Kept in step with it.")).toBeInTheDocument();

    fireEvent.click(within(row).getByRole("button", { name: "Unlink" }));

    expect(
      await screen.findByTestId("processing-library-notice"),
    ).toHaveTextContent(
      "Movies 4K is now a Weir-only workflow. Its folders and settings were not changed.",
    );
    expect(server.to(unlinkRoute(1))).toHaveLength(1);
    expect(server.to(unlinkRoute(1))[0]!.body).toEqual({
      csrf_token: CSRF_TOKEN,
    });
    expect(server.to(unlinkRoute(2))).toHaveLength(0);
    await waitFor(() =>
      expect(
        within(screen.getByTestId("processing-library-1")).queryByRole(
          "button",
          { name: "Unlink" },
        ),
      ).not.toBeInTheDocument(),
    );
    expect(screen.queryByText("Kept in step with it.")).not.toBeInTheDocument();
  });

  it("holds the button while the request is on its way", async () => {
    let answer: (value: unknown) => void = () => undefined;
    server = serveWorkflows({
      workflows: [imported],
      extra: {
        [unlinkRoute(1)]: () =>
          new Promise((resolve) => {
            answer = resolve;
          }),
      },
    });
    const row = await openTab();
    const unlink = within(row).getByRole("button", { name: "Unlink" });

    fireEvent.click(unlink);

    await waitFor(() => expect(unlink).toBeDisabled());
    answer(library({ id: 1, name: "Movies 4K" }));
    await waitFor(() => expect(unlink).toBeEnabled());
  });

  it("says why when it could not be unlinked, and keeps the link", async () => {
    server = serveWorkflows({
      workflows: [imported],
      extra: {
        [unlinkRoute(1)]: () =>
          jsonAnswer(
            { detail: "Deluno is still sending Movies 4K files." },
            409,
          ),
      },
    });
    const row = await openTab();

    fireEvent.click(within(row).getByRole("button", { name: "Unlink" }));

    expect(
      await screen.findByTestId("processing-library-notice"),
    ).toHaveTextContent("Deluno is still sending Movies 4K files.");
    expect(within(row).getByRole("button", { name: "Unlink" })).toBeEnabled();
  });

  it("is not offered to a viewer", async () => {
    server = serveWorkflows({ workflows: [imported], role: "viewer" });
    const row = await openTab();

    await waitFor(() =>
      expect(within(row).getByRole("button", { name: "Edit" })).toBeDisabled(),
    );
    expect(within(row).getByRole("button", { name: "Unlink" })).toBeDisabled();
  });

  it("is not on a workflow that was never imported", async () => {
    server = serveWorkflows({ workflows: [library({ id: 1 })] });
    const row = await openTab();

    expect(
      within(row).queryByRole("button", { name: "Unlink" }),
    ).not.toBeInTheDocument();
  });
});
