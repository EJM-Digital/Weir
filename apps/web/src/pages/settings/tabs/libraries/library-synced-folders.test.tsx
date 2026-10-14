import {
  cleanup,
  fireEvent,
  render,
  screen,
  within,
} from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";

import * as managerApi from "../../../../lib/media-managers/media-managers-api";
import type { MediaManagerConnection } from "../../../../lib/media-managers/media-managers-api";
import * as api from "../../../../lib/processing/libraries-api";
import { LibrariesTab } from "./libraries-tab";
import { asOperator, library, wrapper } from "./library-test-fixtures";

function deluno(): MediaManagerConnection {
  return {
    id: 5,
    kind: "deluno",
    name: "Deluno",
    enabled: true,
    base_url: "http://deluno",
    api_key_is_saved: true,
    webhook_secret_is_set: true,
    webhook_url_path: "/hook",
    downloaded_scan_enabled: false,
    unsigned_webhook_warning: null,
    last_test_ok: true,
    last_test_at: "2026-09-29T10:00:00Z",
    last_test_detail: null,
  };
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

async function openEditorFor(workflow: ReturnType<typeof library>) {
  asOperator();
  vi.mocked(managerApi.fetchMediaManagerConnections).mockResolvedValue([
    deluno(),
  ]);
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([workflow]);
  render(<LibrariesTab />, { wrapper });
  fireEvent.click(await screen.findByRole("button", { name: "Edit" }));
  return await screen.findByTestId("processing-library-form");
}

const synced = {
  id: 3,
  name: "Movies",
  watched_folder: "C:\\Downloads\\Completed\\Movies",
  output_folder: "C:\\Weir\\Ready\\Movies",
  manager_connection_ids: [5],
  discovered_from_connection_id: 5,
  discovered_library_key: "lib-movies",
  folders_synced_from_connection_id: 5,
};

it("shows the watched and output folders of a workflow synced from Deluno as Deluno's, and keeps the work folder editable", async () => {
  const form = await openEditorFor(library(synced));

  const watched = within(form).getByLabelText("Watched folder");
  const output = within(form).getByLabelText("Output folder");
  expect(watched).toHaveValue("C:\\Downloads\\Completed\\Movies");
  expect(watched).toHaveAttribute("readonly");
  expect(output).toHaveValue("C:\\Weir\\Ready\\Movies");
  expect(output).toHaveAttribute("readonly");
  expect(
    within(form).getAllByText("From Deluno; change it in Deluno."),
  ).toHaveLength(2);
  expect(within(form).getByLabelText("Work folder")).not.toHaveAttribute(
    "readonly",
  );
  // Browse for the watched, output and work folders, in that order.
  const browse = within(form).getAllByRole("button", { name: "Browse" });
  expect(
    browse.map((button) => (button as HTMLButtonElement).disabled),
  ).toEqual([true, true, false]);
});

it("says what to do when Deluno has not given a folder yet", async () => {
  const form = await openEditorFor(library({ ...synced, watched_folder: "" }));

  expect(
    within(form).getByText(
      "Deluno hasn't said yet; set it in Deluno and Weir will fill it in.",
    ),
  ).toBeInTheDocument();
  expect(within(form).getByLabelText("Watched folder")).toHaveAttribute(
    "readonly",
  );
});

it("makes the folders editable again once the workflow is unlinked", async () => {
  const form = await openEditorFor(library(synced));

  fireEvent.click(within(form).getByText("Media manager"));
  fireEvent.click(await within(form).findByTestId("library-unlink"));

  expect(within(form).getByLabelText("Watched folder")).not.toHaveAttribute(
    "readonly",
  );
  expect(
    within(form).queryByText("From Deluno; change it in Deluno."),
  ).toBeNull();
});

it("leaves the folders of a workflow that is not synced editable", async () => {
  const form = await openEditorFor(
    library({
      id: 4,
      watched_folder: "/downloads/movies",
      output_folder: "/media/movies",
    }),
  );

  expect(within(form).getByLabelText("Watched folder")).not.toHaveAttribute(
    "readonly",
  );
  expect(within(form).queryByText(/change it in/)).toBeNull();
});
