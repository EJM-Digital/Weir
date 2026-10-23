import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import * as downloadClientsApi from "../../lib/download-clients/download-clients-api";
import type { DownloadClientConnection } from "../../lib/download-clients/download-clients-api";
import { authKeys } from "../../lib/auth/query-keys";
import * as managersApi from "../../lib/media-managers/media-managers-api";
import type { MediaManagerConnection } from "../../lib/media-managers/media-managers-api";
import * as librariesApi from "../../lib/processing/libraries-api";
import type { ProcessingLibrary } from "../../lib/processing/libraries-api";
import * as setupApi from "../../lib/processing/library-setup-api";
import type {
  LibrarySuggestions,
  ProposedLibraryChecks,
} from "../../lib/processing/library-setup-api";
import { processingKeys } from "../../lib/processing/query-keys";
import { settingsKeys } from "../../lib/settings/query-keys";
import { useFakeClock, useRealClock } from "../../test/fake-clock";
import { SetupWizardPage } from "./setup-wizard-page";
import { CEILING, TEST_CEILING_MS } from "../../test/wizard-test-server";

vi.setConfig({ testTimeout: TEST_CEILING_MS });

const { navigateMock, saveSettingsMock } = vi.hoisted(() => ({
  navigateMock: vi.fn(),
  saveSettingsMock: vi.fn(),
}));

vi.mock("react-router-dom", async (importOriginal) => {
  const actual = await importOriginal<typeof import("react-router-dom")>();
  return { ...actual, useNavigate: () => navigateMock };
});

vi.mock("../../lib/settings/queries", async (importOriginal) => {
  const actual =
    await importOriginal<typeof import("../../lib/settings/queries")>();
  return {
    ...actual,
    useAppSettingsSaveMutation: () => ({
      isPending: false,
      mutateAsync: saveSettingsMock,
    }),
  };
});

function seededLibrary(over: Partial<ProcessingLibrary>): ProcessingLibrary {
  return {
    id: 1,
    name: "Movies",
    enabled: true,
    media_type: "movie",
    display_order: 0,
    watched_folder: "",
    work_folder: "",
    output_folder: "",
    rule_set_id: 1,
    manager_connection_ids: [],
    ...over,
  } as ProcessingLibrary;
}

function managerConnection(
  over: Partial<MediaManagerConnection>,
): MediaManagerConnection {
  return {
    id: 5,
    kind: "deluno",
    name: "Deluno",
    enabled: true,
    base_url: "http://10.0.0.51:5000",
    api_key_is_saved: true,
    webhook_secret_is_set: false,
    webhook_url_path: "/api/v1/intake/webhook/deluno",
    downloaded_scan_enabled: false,
    unsigned_webhook_warning: null,
    last_test_ok: true,
    last_test_at: "2026-09-29T10:00:00Z",
    last_test_detail: "Connected.",
    ...over,
  };
}

function clientConnection(
  over: Partial<DownloadClientConnection>,
): DownloadClientConnection {
  return {
    id: 8,
    kind: "sabnzbd",
    name: "SABnzbd",
    enabled: true,
    base_url: "http://10.0.0.60:8080",
    username: null,
    password_is_saved: false,
    api_key_is_saved: true,
    last_test_ok: true,
    last_test_at: "2026-09-29T10:00:00Z",
    last_test_detail: "Connected.",
    ...over,
  };
}

const SEEDED_LIBRARIES = [
  seededLibrary({ id: 1, name: "Movies", media_type: "movie" }),
  seededLibrary({ id: 2, name: "TV", media_type: "tv", display_order: 1 }),
];

const DELUNO_SUGGESTIONS: LibrarySuggestions = {
  libraries: [
    {
      library_id: 1,
      name: "Movies",
      media_type: "movie",
      watched_folder: "C:\\Downloads\\Completed\\Movies",
      output_folder: "E:\\Weir\\Ready\\Movies",
      source_label: "Deluno",
      manager_connection_ids: [5],
    },
    {
      library_id: 2,
      name: "TV",
      media_type: "tv",
      watched_folder: "C:\\Downloads\\Completed\\TV",
      output_folder: "E:\\Weir\\Ready\\TV",
      source_label: "Deluno",
      manager_connection_ids: [5],
    },
  ],
  notes: [],
};

function passingCheck(): ProposedLibraryChecks {
  const chain = {
    library_id: 0,
    local: {
      ready: true,
      lines: [{ state: "ok" as const, text: "Weir can read the folder." }],
    },
    managers: [],
    download_clients: [],
    ready: true,
  };
  return {
    movie: { problem: null, chain },
    tv: { problem: null, chain },
  };
}

type Scenario = {
  managers: MediaManagerConnection[];
  clients: DownloadClientConnection[];
  suggestions: LibrarySuggestions;
  checks: ProposedLibraryChecks;
};

let scenario: Scenario;
let createLibrary: ReturnType<typeof vi.spyOn>;
let updateLibrary: ReturnType<typeof vi.spyOn>;

function renderWizard() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: Infinity },
      mutations: { retry: false },
    },
  });
  client.setQueryData(authKeys.me, { id: 1, username: "admin", role: "admin" });
  // A Docker install is not asked whether to check for updates, so these tests are about the rest of setup.
  client.setQueryData(settingsKeys.updateStatus, { install_type: "docker" });
  client.setQueryData(settingsKeys.app, {
    setup_wizard_state: "pending",
    app_timezone: "UTC",
    log_retention_days: 30,
    configuration_backup_enabled: false,
    configuration_backup_interval_hours: 24,
    configuration_backup_preferred_time: "02:00",
    configuration_backup_last_run_at: null,
    updated_at: "2026-04-23T00:00:00Z",
  });
  client.setQueryData(processingKeys.libraries, SEEDED_LIBRARIES);
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <SetupWizardPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function choose(name: string | RegExp) {
  fireEvent.click(screen.getByRole("radio", { name }));
}

beforeEach(() => {
  useFakeClock();
  navigateMock.mockReset();
  saveSettingsMock.mockReset().mockResolvedValue({});
  scenario = {
    managers: [],
    clients: [],
    suggestions: DELUNO_SUGGESTIONS,
    checks: passingCheck(),
  };
  vi.spyOn(managersApi, "fetchMediaManagerConnections").mockImplementation(
    async () => scenario.managers,
  );
  vi.spyOn(
    downloadClientsApi,
    "fetchDownloadClientConnections",
  ).mockImplementation(async () => scenario.clients);
  vi.spyOn(setupApi, "fetchLibrarySuggestions").mockImplementation(
    async () => scenario.suggestions,
  );
  vi.spyOn(setupApi, "checkProposedLibraries").mockImplementation(
    async () => scenario.checks,
  );
  vi.spyOn(librariesApi, "fetchProcessingLibraries").mockResolvedValue(
    SEEDED_LIBRARIES,
  );
  createLibrary = vi
    .spyOn(librariesApi, "createProcessingLibrary")
    .mockResolvedValue(seededLibrary({ id: 9 }));
  updateLibrary = vi
    .spyOn(librariesApi, "updateProcessingLibrary")
    .mockResolvedValue(seededLibrary({ id: 1 }));
});

afterEach(() => {
  // Unmounting first stops the folder check's settle timer, so nothing asks the api once its stubs are gone.
  cleanup();
  vi.restoreAllMocks();
  useRealClock();
});

async function foundLibraries() {
  return within(await screen.findByTestId("setup-wizard-found", {}, CEILING));
}

describe("first run: connect Deluno first", () => {
  it("connects Deluno, tests it, and offers the workflows it reports", async () => {
    const create = vi
      .spyOn(managersApi, "createMediaManagerConnection")
      .mockImplementation(async () => {
        scenario.managers = [
          managerConnection({ last_test_ok: null, last_test_at: null }),
        ];
        return scenario.managers[0];
      });
    const test = vi
      .spyOn(managersApi, "testMediaManagerConnection")
      .mockImplementation(async () => {
        scenario.managers = [managerConnection({})];
        return {
          connection_id: 5,
          ok: true,
          detail: "Connected.",
          checked_at: "2026-09-29T10:00:00Z",
        };
      });
    renderWizard();

    choose("Deluno");
    fireEvent.change(
      await screen.findByTestId("media-manager-base-url", {}, CEILING),
      {
        target: { value: "http://10.0.0.51:5000" },
      },
    );
    fireEvent.change(screen.getByTestId("media-manager-api-key"), {
      target: { value: "secret" },
    });
    fireEvent.click(screen.getByTestId("media-manager-save"));

    expect(
      await screen.findByText("✓ Connected", {}, CEILING),
    ).toBeInTheDocument();
    expect(create).toHaveBeenCalledWith(
      expect.objectContaining({
        kind: "deluno",
        base_url: "http://10.0.0.51:5000",
      }),
    );
    expect(test).toHaveBeenCalledWith(5);
    const found = await foundLibraries();
    expect(found.getByRole("checkbox", { name: /Movies/ })).toBeChecked();
    expect(
      found.getByRole("textbox", { name: "Movies watched folder" }),
    ).toHaveValue("C:\\Downloads\\Completed\\Movies");
    expect(
      found.getByRole("textbox", { name: "TV output folder" }),
    ).toHaveValue("E:\\Weir\\Ready\\TV");
  });

  it("offers only the media managers the answer stands for, without asking which one", async () => {
    renderWizard();

    choose("Deluno");

    expect(
      await screen.findByTestId("media-manager-base-url", {}, CEILING),
    ).toBeInTheDocument();
    expect(screen.queryByTestId("media-manager-kind")).not.toBeInTheDocument();
  });

  it("badges each offered workflow with the Deluno it is linked to", async () => {
    scenario.managers = [managerConnection({ name: "Deluno on MEDIA-PC" })];
    renderWizard();

    choose("Deluno");
    const found = await foundLibraries();

    expect(
      found
        .getAllByTestId("workflow-kind-badge")
        .map((badge) => badge.textContent),
    ).toEqual(["Linked to Deluno on MEDIA-PC", "Linked to Deluno on MEDIA-PC"]);
  });

  it("saves the offered workflows into the empty ones, linked to Deluno, and drops the connect step from What's next", async () => {
    scenario.managers = [managerConnection({})];
    renderWizard();

    choose("Deluno");
    await foundLibraries();
    fireEvent.click(screen.getByRole("button", { name: "Finish setup" }));

    await waitFor(
      () => expect(updateLibrary).toHaveBeenCalledTimes(2),
      CEILING,
    );
    expect(updateLibrary).toHaveBeenCalledWith(
      1,
      expect.objectContaining({
        name: "Movies",
        watched_folder: "C:\\Downloads\\Completed\\Movies",
        output_folder: "E:\\Weir\\Ready\\Movies",
        manager_connection_ids: [5],
      }),
    );
    expect(updateLibrary).toHaveBeenCalledWith(
      2,
      expect.objectContaining({ name: "TV", manager_connection_ids: [5] }),
    );
    expect(createLibrary).not.toHaveBeenCalled();
    expect(
      await screen.findByRole("heading", { name: "What's next" }, CEILING),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("link", { name: "Connect Sonarr, Radarr or Deluno" }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole("link", {
        name: "Finish setting up Sonarr and Radarr",
      }),
    ).not.toBeInTheDocument();
    expect(saveSettingsMock).toHaveBeenCalledWith(
      expect.objectContaining({ setup_wizard_state: "completed" }),
    );
  });

  it("creates a new workflow when the first one of its type is already in use", async () => {
    scenario.managers = [managerConnection({})];
    scenario.suggestions = {
      libraries: [
        {
          ...DELUNO_SUGGESTIONS.libraries[0],
          library_id: null,
          name: "Movies (2)",
        },
      ],
      notes: [],
    };
    renderWizard();

    choose("Deluno");
    await foundLibraries();
    fireEvent.click(screen.getByRole("button", { name: "Finish setup" }));

    await waitFor(
      () => expect(createLibrary).toHaveBeenCalledTimes(1),
      CEILING,
    );
    expect(createLibrary).toHaveBeenCalledWith({
      name: "Movies (2)",
      media_type: "movie",
      watched_folder: "C:\\Downloads\\Completed\\Movies",
      output_folder: "E:\\Weir\\Ready\\Movies",
      manager_connection_ids: [5],
    });
    expect(updateLibrary).not.toHaveBeenCalled();
  });

  it("creates nothing for a workflow that is unticked, and saves the folders as edited", async () => {
    scenario.managers = [managerConnection({})];
    renderWizard();

    choose("Deluno");
    const found = await foundLibraries();
    fireEvent.click(found.getByRole("checkbox", { name: /TV/ }));
    fireEvent.change(
      found.getByRole("textbox", { name: "Movies output folder" }),
      {
        target: { value: "F:\\Ready\\Movies" },
      },
    );
    fireEvent.click(screen.getByRole("button", { name: "Finish setup" }));

    await waitFor(
      () => expect(updateLibrary).toHaveBeenCalledTimes(1),
      CEILING,
    );
    expect(updateLibrary).toHaveBeenCalledWith(
      1,
      expect.objectContaining({ output_folder: "F:\\Ready\\Movies" }),
    );
    expect(createLibrary).not.toHaveBeenCalled();
  });

  it("says what to set in Deluno when it gives no folders, offers no overlapping workflow, and still lets setup finish", async () => {
    scenario.managers = [managerConnection({})];
    scenario.suggestions = {
      libraries: [],
      notes: [
        "Deluno does not say where Movies' downloads arrive. Set the downloads folder in Deluno and Weir will pick it up.",
        "Deluno does not say where TV's downloads arrive. Set the downloads folder in Deluno and Weir will pick it up.",
      ],
    };
    renderWizard();

    choose("Deluno");
    const found = await foundLibraries();

    expect(
      found.getAllByText(/Set the downloads folder in Deluno/),
    ).toHaveLength(2);
    expect(found.queryByRole("textbox")).not.toBeInTheDocument();
    expect(
      found.getByRole("button", { name: "Pick the folders yourself" }),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Finish setup" }));

    expect(
      await screen.findByRole("heading", { name: "What's next" }, CEILING),
    ).toBeInTheDocument();
    expect(createLibrary).not.toHaveBeenCalled();
    expect(updateLibrary).not.toHaveBeenCalled();
    expect(saveSettingsMock).toHaveBeenCalledWith(
      expect.objectContaining({ setup_wizard_state: "completed" }),
    );
  });

  it("says plainly when Deluno cannot be reached, and lets the person go to typing the folders", async () => {
    scenario.managers = [
      managerConnection({
        last_test_ok: false,
        last_test_detail: "Nothing answered at that address.",
      }),
    ];
    renderWizard();

    choose("Deluno");

    expect(
      await screen.findByText("Weir could not connect to Deluno.", {}, CEILING),
    ).toBeInTheDocument();
    expect(
      screen.getByText("Nothing answered at that address."),
    ).toBeInTheDocument();
    expect(screen.queryByTestId("setup-wizard-found")).not.toBeInTheDocument();

    choose(/Neither/);

    expect(
      screen.getByRole("textbox", { name: "Movies watched folder" }),
    ).toHaveValue("");
    expect(
      screen.queryByText("Weir could not connect to Deluno."),
    ).not.toBeInTheDocument();
  });

  it("shows a check problem under the workflow it belongs to and will not finish until it is fixed", async () => {
    scenario.managers = [managerConnection({})];
    scenario.checks = {
      ...passingCheck(),
      movie: {
        problem:
          "Weir can't use its own data folder as a watched folder. Choose a different folder.",
        chain: null,
      },
    };
    renderWizard();

    choose("Deluno");
    await foundLibraries();

    expect(
      await screen.findByTestId(
        "setup-wizard-found-movie-problem",
        {},
        CEILING,
      ),
    ).toHaveTextContent("Weir can't use its own data folder");
    fireEvent.click(screen.getByRole("button", { name: "Finish setup" }));

    expect(await screen.findByRole("alert", {}, CEILING)).toHaveTextContent(
      "Weir can't use its own data folder",
    );
    expect(updateLibrary).not.toHaveBeenCalled();
    expect(saveSettingsMock).not.toHaveBeenCalled();
  });

  it("lists what still needs fixing in a workflow's folder chain, without stopping Finish", async () => {
    scenario.managers = [managerConnection({})];
    scenario.checks = {
      ...passingCheck(),
      tv: {
        problem: null,
        chain: {
          library_id: 0,
          local: {
            ready: false,
            lines: [
              {
                state: "problem",
                text: "The watched folder C:\\Downloads\\Completed\\TV does not exist. Create it, or point this workflow at a folder that does.",
              },
            ],
          },
          managers: [],
          download_clients: [],
          ready: false,
        },
      },
    };
    renderWizard();

    choose("Deluno");
    await foundLibraries();

    expect(
      await screen.findByText(/does not exist\. Create it/, {}, CEILING),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Finish setup" }));
    await waitFor(
      () => expect(updateLibrary).toHaveBeenCalledTimes(2),
      CEILING,
    );
  });

  it("shows a line Weir could not verify as not verified, not as fine and not as a problem", async () => {
    scenario.managers = [managerConnection({})];
    scenario.checks = {
      ...passingCheck(),
      movie: {
        problem: null,
        chain: {
          library_id: 0,
          local: { ready: true, lines: [] },
          managers: [
            {
              connection_id: 5,
              kind: "deluno",
              name: "Deluno",
              label: "Deluno",
              flow: "handoff",
              ready: true,
              lines: [
                {
                  state: "unverified",
                  text: "Deluno says its Movies library downloads here. Weir can't see where each download client really saves.",
                },
              ],
            },
          ],
          download_clients: [],
          ready: true,
        },
      },
    };
    renderWizard();

    choose("Deluno");
    await foundLibraries();

    expect(
      await screen.findByText(
        /Weir can't see where each download client/,
        {},
        CEILING,
      ),
    ).toBeInTheDocument();
    expect(screen.getByText("Not verified:")).toBeInTheDocument();
    expect(screen.queryByText("Fine:")).not.toBeInTheDocument();
  });
});

describe("first run: connect Sonarr and Radarr first", () => {
  const SONARR_RADARR: LibrarySuggestions = {
    libraries: [
      {
        library_id: 1,
        name: "Movies",
        media_type: "movie",
        watched_folder: "/downloads/movies",
        output_folder: "/downloads/Weir Ready/Movies",
        source_label: "Radarr",
        manager_connection_ids: [11],
      },
      {
        library_id: 2,
        name: "TV",
        media_type: "tv",
        watched_folder: "/downloads/tv",
        output_folder: "/downloads/Weir Ready/TV",
        source_label: "Sonarr",
        manager_connection_ids: [10],
      },
    ],
    notes: [],
  };

  beforeEach(() => {
    scenario.managers = [
      managerConnection({ id: 10, kind: "sonarr", name: "Sonarr" }),
      managerConnection({ id: 11, kind: "radarr", name: "Radarr" }),
    ];
    scenario.suggestions = SONARR_RADARR;
  });

  it("shows both connections and links each workflow only to the manager that covers it", async () => {
    renderWizard();

    choose("Sonarr / Radarr");
    const found = await foundLibraries();

    expect(screen.getAllByTestId("setup-wizard-connection")).toHaveLength(2);
    expect(found.getByText(/folders from Radarr/)).toBeInTheDocument();
    expect(found.getByText(/folders from Sonarr/)).toBeInTheDocument();
    expect(
      found
        .getAllByTestId("workflow-kind-badge")
        .map((badge) => badge.textContent),
    ).toEqual(["Linked to Radarr", "Linked to Sonarr"]);

    fireEvent.click(screen.getByRole("button", { name: "Finish setup" }));

    await waitFor(
      () => expect(updateLibrary).toHaveBeenCalledTimes(2),
      CEILING,
    );
    expect(updateLibrary).toHaveBeenCalledWith(
      1,
      expect.objectContaining({ manager_connection_ids: [11] }),
    );
    expect(updateLibrary).toHaveBeenCalledWith(
      2,
      expect.objectContaining({ manager_connection_ids: [10] }),
    );
  });

  it("points at the remaining Sonarr and Radarr steps on What's next", async () => {
    renderWizard();

    choose("Sonarr / Radarr");
    await foundLibraries();
    fireEvent.click(screen.getByRole("button", { name: "Finish setup" }));

    expect(
      await screen.findByRole(
        "link",
        {
          name: "Finish setting up Sonarr and Radarr",
        },
        CEILING,
      ),
    ).toHaveAttribute("href", "/setup/workflows");
    expect(
      screen.queryByRole("link", { name: "Connect Sonarr, Radarr or Deluno" }),
    ).not.toBeInTheDocument();
  });

  it("offers Sonarr and Radarr, and no other media manager, in the connect form", async () => {
    scenario.managers = [];
    renderWizard();

    choose("Sonarr / Radarr");

    const kinds = await screen.findByTestId("media-manager-kind", {}, CEILING);
    expect(
      within(kinds)
        .getAllByRole("option")
        .map((option) => option.textContent),
    ).toEqual(["Sonarr", "Radarr"]);
  });

  it("says why a manager offered nothing, in the words the server used", async () => {
    scenario.suggestions = {
      libraries: [],
      notes: ["Radarr does not say where its downloads are saved."],
    };
    renderWizard();

    choose("Sonarr / Radarr");

    expect(
      await screen.findByText(
        "Radarr does not say where its downloads are saved.",
        {},
        CEILING,
      ),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/Weir could not find a folder to start a workflow from/),
    ).toBeInTheDocument();
  });
});

describe("first run: connect a download client first", () => {
  it("offers workflows from the client's folders and links them to no media manager", async () => {
    scenario.clients = [clientConnection({})];
    scenario.suggestions = {
      libraries: [
        {
          library_id: 1,
          name: "Movies",
          media_type: "movie",
          watched_folder: "/downloads/complete/movies",
          output_folder: "/downloads/complete/Weir Ready/Movies",
          source_label: "SABnzbd",
          manager_connection_ids: [],
        },
      ],
      notes: [],
    };
    renderWizard();

    choose(/A download client/);
    const found = await foundLibraries();
    expect(found.getByText(/folders from SABnzbd/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Finish setup" }));

    await waitFor(
      () => expect(updateLibrary).toHaveBeenCalledTimes(1),
      CEILING,
    );
    expect(updateLibrary).toHaveBeenCalledWith(
      1,
      expect.objectContaining({
        watched_folder: "/downloads/complete/movies",
        manager_connection_ids: [],
      }),
    );
  });

  describe("the badge on each offered workflow", () => {
    function suggestionLinkedTo(
      managerConnectionIds: number[],
    ): LibrarySuggestions {
      return {
        libraries: [
          {
            library_id: 1,
            name: "Movies",
            media_type: "movie",
            watched_folder: "/downloads/complete/movies",
            output_folder: "/downloads/complete/Weir Ready/Movies",
            source_label: "qBittorrent",
            manager_connection_ids: managerConnectionIds,
          },
        ],
        notes: [],
      };
    }

    it("names the connected Deluno a suggestion links to, though a download client was chosen", async () => {
      scenario.clients = [clientConnection({})];
      scenario.managers = [managerConnection({ name: "Deluno on MEDIA-PC" })];
      scenario.suggestions = suggestionLinkedTo([5]);
      renderWizard();

      choose(/A download client/);
      const found = await foundLibraries();

      expect(found.getByTestId("workflow-kind-badge")).toHaveTextContent(
        "Linked to Deluno on MEDIA-PC",
      );
    });

    it("says a media manager was removed only when the linked connection no longer exists", async () => {
      scenario.clients = [clientConnection({})];
      scenario.managers = [managerConnection({ name: "Deluno on MEDIA-PC" })];
      scenario.suggestions = suggestionLinkedTo([99]);
      renderWizard();

      choose(/A download client/);
      const found = await foundLibraries();

      expect(found.getByTestId("workflow-kind-badge")).toHaveTextContent(
        "Linked to a removed media manager",
      );
    });
  });

  it("tests a download client by itself when the server has no result for it", async () => {
    scenario.clients = [
      clientConnection({
        last_test_ok: undefined,
        last_test_detail: undefined,
      }),
    ];
    const test = vi
      .spyOn(downloadClientsApi, "testDownloadClientConnection")
      .mockImplementation(async () => {
        scenario.clients = [clientConnection({})];
        return {
          connection_id: 8,
          ok: true,
          detail: "Connected.",
          checked_at: "2026-09-29T10:00:00Z",
        };
      });
    renderWizard();

    choose(/A download client/);

    expect(
      await screen.findByText("✓ Connected", {}, CEILING),
    ).toBeInTheDocument();
    expect(test).toHaveBeenCalledTimes(1);
  });

  it("says a download client could not be tested when the test itself fails", async () => {
    scenario.clients = [clientConnection({})];
    vi.spyOn(
      downloadClientsApi,
      "testDownloadClientConnection",
    ).mockRejectedValue(new Error("Weir could not reach the server."));
    renderWizard();

    choose(/A download client/);
    fireEvent.click(
      await screen.findByRole("button", { name: "Test" }, CEILING),
    );

    expect(
      await screen.findByText("Weir could not reach the server.", {}, CEILING),
    ).toBeInTheDocument();
  });

  it("asks before removing a download client, and keeps it when the person says to", async () => {
    scenario.clients = [clientConnection({})];
    const remove = vi.spyOn(
      downloadClientsApi,
      "deleteDownloadClientConnection",
    );
    renderWizard();

    choose(/A download client/);
    fireEvent.click(
      await screen.findByRole("button", { name: "Remove" }, CEILING),
    );
    const dialog = await screen.findByRole("dialog", {}, CEILING);
    expect(dialog).toHaveTextContent(/^Remove .*\?/);
    fireEvent.click(within(dialog).getByRole("button", { name: "Keep it" }));

    await waitFor(() =>
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument(),
    );
    expect(remove).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Remove" })).toBeVisible();
  });

  it("connects a download client with the form Settings uses", async () => {
    const create = vi
      .spyOn(downloadClientsApi, "createDownloadClientConnection")
      .mockImplementation(async () => {
        scenario.clients = [
          clientConnection({ last_test_ok: null, last_test_at: null }),
        ];
        return scenario.clients[0];
      });
    vi.spyOn(
      downloadClientsApi,
      "testDownloadClientConnection",
    ).mockImplementation(async () => {
      scenario.clients = [clientConnection({})];
      return {
        connection_id: 8,
        ok: true,
        detail: "Connected.",
        checked_at: "2026-09-29T10:00:00Z",
      };
    });
    renderWizard();

    choose(/A download client/);
    fireEvent.change(
      await screen.findByTestId("download-client-base-url", {}, CEILING),
      {
        target: { value: "http://10.0.0.60:8080" },
      },
    );
    fireEvent.change(screen.getByTestId("download-client-api-key"), {
      target: { value: "key" },
    });
    fireEvent.click(screen.getByTestId("download-client-save"));

    expect(
      await screen.findByText("✓ Connected", {}, CEILING),
    ).toBeInTheDocument();
    expect(create).toHaveBeenCalledWith(
      expect.objectContaining({
        kind: "sabnzbd",
        base_url: "http://10.0.0.60:8080",
      }),
    );
  });
});
