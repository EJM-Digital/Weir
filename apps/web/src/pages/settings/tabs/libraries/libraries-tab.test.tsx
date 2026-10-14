import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";

import * as api from "../../../../lib/processing/libraries-api";
import * as managersApi from "../../../../lib/processing/library-managers-api";
import * as managerApi from "../../../../lib/media-managers/media-managers-api";
import { LibrariesTab } from "./libraries-tab";
import {
  asOperator,
  asViewer,
  library,
  wrapper,
} from "./library-test-fixtures";

afterEach(() => {
  vi.restoreAllMocks();
});

it("lists every library, not just Movies and TV", async () => {
  asOperator();
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([
    library(),
    library({ id: 2, name: "TV", media_type: "tv", display_order: 1 }),
    library({ id: 3, name: "Movies 4K", display_order: 2 }),
  ]);

  render(<LibrariesTab />, { wrapper });

  expect(await screen.findByText("Movies 4K")).toBeInTheDocument();
  expect(screen.getByText("TV")).toBeInTheDocument();
});

it("discovers and imports selected manager libraries", async () => {
  asOperator();
  vi.mocked(managerApi.fetchMediaManagerConnections).mockResolvedValue([
    {
      id: 7,
      kind: "deluno",
      name: "Deluno",
      enabled: true,
      base_url: "http://deluno",
      api_key_is_saved: true,
      webhook_secret_is_set: false,
      webhook_url_path: "/hook",
      downloaded_scan_enabled: false,
      unsigned_webhook_warning: null,
      last_test_ok: true,
      last_test_at: null,
      last_test_detail: null,
    },
  ]);
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([library()]);
  vi.spyOn(managersApi, "discoverProcessingLibraries").mockResolvedValue([
    {
      key: "movies-4k",
      name: "Movies 4K",
      media_type: "movie",
      root_path: "/manager/movies-4k",
      already_imported: false,
      local_path_problem: null,
      processes_before_import: true,
      output_path: "/manager/refined-4k",
      output_path_problem: null,
    },
  ]);
  const imported = vi
    .spyOn(managersApi, "importDiscoveredProcessingLibraries")
    .mockResolvedValue([library({ id: 4, name: "Movies 4K" })]);

  render(<LibrariesTab />, { wrapper });
  fireEvent.change(await screen.findByLabelText("Media manager"), {
    target: { value: "7" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Discover workflows" }));
  fireEvent.click(await screen.findByLabelText(/Movies 4K/));
  fireEvent.click(screen.getByRole("button", { name: "Import selected" }));

  await waitFor(() => {
    expect(imported).toHaveBeenCalledWith(7, ["movies-4k"]);
    expect(screen.getByTestId("processing-library-notice")).toHaveTextContent(
      /1 workflow was imported/,
    );
  });
});

it("shows how much work is in flight, so a refusal makes sense", async () => {
  asOperator();
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([
    library({ active_job_count: 3 }),
  ]);

  render(<LibrariesTab />, { wrapper });

  expect(await screen.findByText(/3 in progress/)).toBeInTheDocument();
});

it("asks before removing a library, naming it and its folder", async () => {
  asOperator();
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([library()]);
  const remove = vi.spyOn(api, "deleteProcessingLibrary");

  render(<LibrariesTab />, { wrapper });
  fireEvent.click(await screen.findByTestId("processing-library-remove-1"));

  const dialog = screen.getByTestId("processing-library-remove-confirm");
  expect(dialog).toHaveTextContent("Remove Movies?");
  expect(dialog).toHaveTextContent(
    "Weir stops watching /srv/movies/in. Its settings and hours go with it. No media file is touched. This cannot be undone.",
  );
  expect(
    screen.getByTestId("processing-library-remove-confirm-confirm"),
  ).toHaveTextContent("Remove workflow");
  expect(remove).not.toHaveBeenCalled();
});

it("removes a library only once the removal is confirmed, and holds the button while it runs", async () => {
  asOperator();
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([library()]);
  let finish: () => void = () => undefined;
  const remove = vi.spyOn(api, "deleteProcessingLibrary").mockReturnValue(
    new Promise<void>((resolve) => {
      finish = resolve;
    }),
  );

  render(<LibrariesTab />, { wrapper });
  fireEvent.click(await screen.findByTestId("processing-library-remove-1"));
  fireEvent.click(
    screen.getByTestId("processing-library-remove-confirm-confirm"),
  );

  await waitFor(() => expect(remove).toHaveBeenCalledWith(1));
  expect(
    screen.getByTestId("processing-library-remove-confirm-confirm"),
  ).toBeDisabled();
  finish();
  await waitFor(() =>
    expect(
      screen.queryByTestId("processing-library-remove-confirm"),
    ).not.toBeInTheDocument(),
  );
});

it("surfaces the refusal reason when a library still has queued work", async () => {
  asOperator();
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([library()]);
  vi.spyOn(api, "deleteProcessingLibrary").mockRejectedValue(
    new Error("Movies still has 2 jobs queued or running."),
  );

  render(<LibrariesTab />, { wrapper });
  fireEvent.click(await screen.findByTestId("processing-library-remove-1"));
  fireEvent.click(
    screen.getByTestId("processing-library-remove-confirm-confirm"),
  );

  expect(await screen.findByRole("alert")).toHaveTextContent(
    /still has 2 jobs queued or running/,
  );
});

it("says the libraries could not be loaded instead of showing none", async () => {
  asOperator();
  vi.spyOn(api, "fetchProcessingLibraries").mockRejectedValue(
    new Error("boom"),
  );

  render(<LibrariesTab />, { wrapper });

  expect(await screen.findByTestId("settings-load-error")).toHaveTextContent(
    "Weir couldn’t load your workflows. Reload the page to try again.",
  );
  expect(screen.queryByText(/No workflows yet/)).not.toBeInTheDocument();
});

it("does not offer editing to a viewer", async () => {
  asViewer();
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([library()]);

  render(<LibrariesTab />, { wrapper });

  expect(await screen.findByText("Movies")).toBeInTheDocument();
  expect(
    screen.queryByTestId("processing-library-add"),
  ).not.toBeInTheDocument();
});

it("says so plainly when nothing is configured yet", async () => {
  asOperator();
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([]);

  render(<LibrariesTab />, { wrapper });

  expect(await screen.findByText(/No workflows yet/)).toBeInTheDocument();
});
