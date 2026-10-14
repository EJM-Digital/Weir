import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, expect, it, vi } from "vitest";

import * as downloadClientsApi from "../../../../lib/download-clients/download-clients-api";
import * as managerApi from "../../../../lib/media-managers/media-managers-api";
import type { MediaManagerConnection } from "../../../../lib/media-managers/media-managers-api";
import * as api from "../../../../lib/processing/libraries-api";
import * as chainApi from "../../../../lib/processing/library-folder-chain-api";
import * as managersApi from "../../../../lib/processing/library-managers-api";
import * as setupApi from "../../../../lib/processing/library-setup-api";
import { LibrariesTab } from "./libraries-tab";
import { asOperator, library, wrapper } from "./library-test-fixtures";

function connection(
  over: Partial<MediaManagerConnection>,
): MediaManagerConnection {
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
    ...over,
  };
}

// The editor keeps loading for a moment after it appears. Unmount it before the stubs are removed, or a
// load that starts in between reaches the network.
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function withConnections(...connections: MediaManagerConnection[]) {
  asOperator();
  vi.mocked(managerApi.fetchMediaManagerConnections).mockResolvedValue(
    connections,
  );
}

function row(name: string) {
  return within(screen.getByRole("row", { name: new RegExp(name) }));
}

it("labels each workflow Weir only or linked to its media manager, and counts them", async () => {
  withConnections(
    connection({}),
    connection({ id: 6, kind: "sonarr", name: "Sonarr" }),
  );
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([
    library({ id: 1, name: "Movies from Deluno", manager_connection_ids: [5] }),
    library({
      id: 2,
      name: "TV from Sonarr",
      media_type: "tv",
      display_order: 1,
      manager_connection_ids: [6],
    }),
    library({ id: 3, name: "Kids", display_order: 2 }),
  ]);

  render(<LibrariesTab />, { wrapper });

  expect(await screen.findByText("Movies from Deluno")).toBeInTheDocument();
  expect(
    row("Movies from Deluno").getByText("Linked to Deluno"),
  ).toBeInTheDocument();
  expect(
    row("TV from Sonarr").getByText("Linked to Sonarr"),
  ).toBeInTheDocument();
  expect(row("Kids").getByText("Weir only")).toBeInTheDocument();
  expect(row("Kids").getByText(/Nothing else is involved/)).toBeInTheDocument();
  expect(
    row("Movies from Deluno").getByText(
      /Deluno hands each finished download to Weir/,
    ),
  ).toBeInTheDocument();
  expect(screen.getByTestId("workflow-kind-counts")).toHaveTextContent(
    "2 linked to a media manager · 1 Weir only",
  );
  expect(
    screen.queryByText(/not linked to a media manager/),
  ).not.toBeInTheDocument();
});

it("says a manager that is gone by that name and still calls the workflow linked", async () => {
  withConnections();
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([
    library({ manager_connection_ids: [99] }),
  ]);

  render(<LibrariesTab />, { wrapper });

  expect(
    await screen.findByText("Linked to a removed media manager"),
  ).toBeInTheDocument();
});

it("keeps a workflow whose manager does not answer as short as the rest, with its full sentence on hover and for screen readers", async () => {
  const sentence =
    "Weir could not reach Deluno at http://deluno. Check the address.";
  withConnections(
    connection({ last_test_ok: false, last_test_detail: sentence }),
  );
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([
    library({
      manager_connection_ids: [5],
      manager_coverage: "unreachable",
    }),
  ]);

  render(<LibrariesTab />, { wrapper });

  const cell = within(await screen.findByTestId("processing-library-1"));
  const status = cell.getByText("Not answering");
  expect(status).toHaveAttribute("aria-hidden", "true");
  expect(status.parentElement).toHaveAttribute("title", sentence);
  expect(cell.getByText(sentence)).toHaveClass("sr-only");
});

it("asks which kind first when adding, and Local folders opens today's form", async () => {
  withConnections(connection({}));
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([library()]);

  render(<LibrariesTab />, { wrapper });
  fireEvent.click(await screen.findByTestId("processing-library-add"));

  const choice = within(await screen.findByTestId("add-workflow-choice"));
  expect(choice.getByRole("radio", { name: /Local folders/ })).toBeChecked();
  expect(
    choice.getByRole("radio", { name: /From a media manager/ }),
  ).not.toBeChecked();
  expect(
    screen.queryByTestId("processing-library-form"),
  ).not.toBeInTheDocument();

  fireEvent.click(screen.getByTestId("add-workflow-continue"));

  const form = await screen.findByTestId("processing-library-form");
  expect(within(form).getByPlaceholderText("Movies 4K")).toHaveValue("");
  expect(
    within(within(form).getByTestId("library-link-section")).getByText(
      "Weir only",
    ),
  ).toBeInTheDocument();
});

it("offers From a media manager only when one is connected", async () => {
  withConnections();
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([library()]);

  render(<LibrariesTab />, { wrapper });
  fireEvent.click(await screen.findByTestId("processing-library-add"));

  const choice = within(await screen.findByTestId("add-workflow-choice"));
  expect(
    choice.getByRole("radio", { name: /From a media manager/ }),
  ).toBeDisabled();
  expect(
    choice.getByRole("link", { name: /Connect Deluno, Sonarr or Radarr/ }),
  ).toHaveAttribute("href", "/setup/connections");
});

it("fills a new workflow's folders in from the media manager and links it, saving only on Save", async () => {
  withConnections(connection({}));
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([library()]);
  vi.spyOn(setupApi, "fetchLibrarySuggestions").mockResolvedValue({
    libraries: [
      {
        library_id: null,
        name: "TV (2)",
        media_type: "tv",
        watched_folder: "/downloads/tv",
        output_folder: "/downloads/Weir Ready/TV",
        source_label: "Deluno",
        manager_connection_ids: [5],
      },
    ],
    notes: [],
  });
  const create = vi
    .spyOn(api, "createProcessingLibrary")
    .mockResolvedValue(library({ id: 9 }));

  render(<LibrariesTab />, { wrapper });
  fireEvent.click(await screen.findByTestId("processing-library-add"));
  fireEvent.click(
    await screen.findByRole("radio", { name: /From a media manager/ }),
  );
  fireEvent.click(screen.getByTestId("add-workflow-continue"));

  const form = await screen.findByTestId("processing-library-form");
  expect(within(form).getByPlaceholderText("Movies 4K")).toHaveValue("TV (2)");
  expect(
    within(within(form).getByTestId("library-link-section")).getByText(
      "Linked to Deluno",
    ),
  ).toBeInTheDocument();
  expect(create).not.toHaveBeenCalled();

  fireEvent.click(screen.getByTestId("processing-library-save"));

  await waitFor(() =>
    expect(create).toHaveBeenCalledWith(
      expect.objectContaining({
        name: "TV (2)",
        media_type: "tv",
        watched_folder: "/downloads/tv",
        output_folder: "/downloads/Weir Ready/TV",
        manager_connection_ids: [5],
      }),
    ),
  );
});

it("opens the empty workflow a suggestion would fill in, instead of adding another", async () => {
  withConnections(connection({}));
  const empty = library({
    id: 1,
    name: "Movies",
    watched_folder: "",
    output_folder: "",
  });
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([empty]);
  vi.spyOn(setupApi, "fetchLibrarySuggestions").mockResolvedValue({
    libraries: [
      {
        library_id: 1,
        name: "Movies",
        media_type: "movie",
        watched_folder: "/downloads/movies",
        output_folder: "/downloads/Weir Ready/Movies",
        source_label: "Deluno",
        manager_connection_ids: [5],
      },
    ],
    notes: [],
  });
  const update = vi
    .spyOn(api, "updateProcessingLibrary")
    .mockResolvedValue(empty);

  render(<LibrariesTab />, { wrapper });
  fireEvent.click(await screen.findByTestId("processing-library-add"));
  fireEvent.click(
    await screen.findByRole("radio", { name: /From a media manager/ }),
  );
  fireEvent.click(screen.getByTestId("add-workflow-continue"));
  await screen.findByTestId("processing-library-form");
  expect(
    screen.getByRole("heading", { name: "Edit workflow" }),
  ).toBeInTheDocument();
  fireEvent.click(screen.getByTestId("processing-library-save"));

  await waitFor(() =>
    expect(update).toHaveBeenCalledWith(
      1,
      expect.objectContaining({
        watched_folder: "/downloads/movies",
        manager_connection_ids: [5],
      }),
    ),
  );
});

it("opens the editor on the chosen media manager, blank, when it reports no folder", async () => {
  withConnections(connection({}));
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([library()]);
  vi.spyOn(setupApi, "fetchLibrarySuggestions").mockResolvedValue({
    libraries: [],
    notes: [],
  });

  render(<LibrariesTab />, { wrapper });
  fireEvent.click(await screen.findByTestId("processing-library-add"));
  fireEvent.click(
    await screen.findByRole("radio", { name: /From a media manager/ }),
  );
  fireEvent.click(screen.getByTestId("add-workflow-continue"));

  const form = await screen.findByTestId("processing-library-form");
  expect(
    within(within(form).getByTestId("library-link-section")).getByText(
      "Linked to Deluno",
    ),
  ).toBeInTheDocument();
  expect(within(form).getByPlaceholderText("Movies 4K")).toHaveValue("");
});

it("presents Weir only as an option in the editor, not a warning, with a way to link a media manager", async () => {
  withConnections(connection({}));
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([
    library({ name: "Kids" }),
  ]);
  const update = vi
    .spyOn(api, "updateProcessingLibrary")
    .mockResolvedValue(library());

  render(<LibrariesTab />, { wrapper });
  fireEvent.click(await screen.findByRole("button", { name: "Edit" }));

  const section = within(await screen.findByTestId("library-link-section"));
  expect(section.getByText("Weir only")).toBeInTheDocument();
  expect(
    section.getByText(
      /Link a media manager if one hands this workflow its downloads/,
    ),
  ).toBeInTheDocument();
  expect(screen.queryByText(/covers Movies/)).not.toBeInTheDocument();
  expect(screen.queryByTestId("library-manager-setup")).not.toBeInTheDocument();
  expect(section.getByTestId("library-link")).toBeDisabled();

  fireEvent.change(section.getByTestId("library-manager-choice"), {
    target: { value: "5" },
  });
  fireEvent.click(section.getByTestId("library-link"));
  expect(
    await within(screen.getByTestId("library-link-section")).findByText(
      "Linked to Deluno",
    ),
  ).toBeInTheDocument();
  fireEvent.click(screen.getByTestId("processing-library-save"));

  await waitFor(() =>
    expect(update).toHaveBeenCalledWith(
      1,
      expect.objectContaining({ manager_connection_ids: [5] }),
    ),
  );
});

it("points to Media managers when there is nothing to link yet", async () => {
  withConnections();
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([
    library({ name: "Kids" }),
  ]);

  render(<LibrariesTab />, { wrapper });
  fireEvent.click(await screen.findByRole("button", { name: "Edit" }));

  const section = within(await screen.findByTestId("library-link-section"));
  expect(
    section.getByRole("link", { name: /Connect Deluno, Sonarr or Radarr/ }),
  ).toBeInTheDocument();
  expect(section.queryByTestId("library-link")).not.toBeInTheDocument();
});

it("says what unlinking does in plain words, and unlinks on Save", async () => {
  withConnections(connection({}));
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([
    library({ name: "TV", media_type: "tv", manager_connection_ids: [5] }),
  ]);
  vi.spyOn(managerApi, "fetchMediaManagerConnections").mockResolvedValue([
    connection({}),
  ]);
  const update = vi
    .spyOn(api, "updateProcessingLibrary")
    .mockResolvedValue(library());

  render(<LibrariesTab />, { wrapper });
  fireEvent.click(await screen.findByRole("button", { name: "Edit" }));

  const section = within(await screen.findByTestId("library-link-section"));
  expect(section.getByText("Linked to Deluno")).toBeInTheDocument();
  expect(
    section.getByText(/Unlinking makes this workflow Weir only/),
  ).toBeInTheDocument();
  expect(
    section.getByText(/everything already set up or cleaned stays as it is/),
  ).toBeInTheDocument();

  fireEvent.click(section.getByTestId("library-unlink"));
  expect(
    await within(screen.getByTestId("library-link-section")).findByText(
      "Weir only",
    ),
  ).toBeInTheDocument();
  fireEvent.click(screen.getByTestId("processing-library-save"));

  await waitFor(() =>
    expect(update).toHaveBeenCalledWith(
      1,
      expect.objectContaining({ manager_connection_ids: [] }),
    ),
  );
});

function wrapperAt(url: string) {
  return function Wrapper({ children }: { children: ReactNode }) {
    const qc = new QueryClient({
      defaultOptions: {
        queries: { retry: false },
        mutations: { retry: false },
      },
    });
    return (
      <MemoryRouter initialEntries={[url]}>
        <QueryClientProvider client={qc}>{children}</QueryClientProvider>
      </MemoryRouter>
    );
  };
}

/** What the editor asks for as it opens, answered with nothing to report. */
function answerEditorLoadsWithNothingToReport() {
  vi.spyOn(chainApi, "fetchLibraryFolderChain").mockResolvedValue({
    library_id: 4,
    local: { ready: true, lines: [] },
    managers: [],
    download_clients: [],
    ready: true,
  });
  vi.spyOn(managersApi, "fetchProcessingManagerSetup").mockResolvedValue({
    media_type: "movie",
    managers: [],
  });
  vi.spyOn(managersApi, "fetchProcessingRejectSupport").mockResolvedValue({
    available: false,
    reason: "No linked media manager supports it.",
  });
  vi.spyOn(
    downloadClientsApi,
    "fetchDownloadClientSuggestions",
  ).mockResolvedValue([]);
}

it("opens a workflow's editor from a link on another page", async () => {
  withConnections(connection({}));
  answerEditorLoadsWithNothingToReport();
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([
    library({ id: 4, name: "Kids" }),
  ]);

  render(<LibrariesTab />, {
    wrapper: wrapperAt("/setup/workflows?edit=4"),
  });

  const form = await screen.findByTestId("processing-library-form");
  expect(within(form).getByDisplayValue("Kids")).toBeInTheDocument();
});

it("opens the add choice already on the media manager it was linked from", async () => {
  withConnections(
    connection({}),
    connection({ id: 6, kind: "sonarr", name: "Sonarr" }),
  );
  vi.spyOn(api, "fetchProcessingLibraries").mockResolvedValue([library()]);

  render(<LibrariesTab />, {
    wrapper: wrapperAt("/setup/workflows?addFrom=6"),
  });

  const choice = within(await screen.findByTestId("add-workflow-choice"));
  expect(
    choice.getByRole("radio", { name: /From a media manager/ }),
  ).toBeChecked();
  expect(
    choice.getByRole("combobox", { name: "Which media manager?" }),
  ).toHaveValue("6");
});
