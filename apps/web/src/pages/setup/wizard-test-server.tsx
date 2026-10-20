import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { expect, vi } from "vitest";

import type { MediaManagerConnection } from "../../lib/media-managers/media-managers-api";
import type { ProcessingLibrary } from "../../lib/processing/libraries-api";
import type { LibrarySuggestions } from "../../lib/processing/library-setup-api";
import { processingKeys } from "../../lib/processing/query-keys";
import type {
  AppSettings,
  UpdateSettingsOut,
  UpdateStatus,
} from "../../lib/settings/types";
import { library } from "../settings/tabs/libraries/library-test-fixtures";
import { SetupWizardPage } from "./setup-wizard-page";

/** One request the wizard sent, with the CSRF token taken out of its body. */
export type SentRequest = {
  method: string;
  path: string;
  body: Record<string, unknown> | null;
};

export type WizardServerState = {
  libraries: ProcessingLibrary[];
  settings: AppSettings;
  managers: MediaManagerConnection[];
  suggestions: LibrarySuggestions;
  /** How many of the next settings saves the server refuses. */
  refuseSettingsSaves: number;
  /** How this Weir was installed; only the Windows app lets its owner choose whether it checks for updates. */
  installType: UpdateStatus["install_type"];
  /** The update choices the tray follows: a new install checks for nothing. */
  updateSettings: UpdateSettingsOut;
};

/** What the server says of updates on an install of this kind; the wizard reads only how it was installed. */
function updateStatusOf(installType: string): UpdateStatus {
  return {
    current_version: "3.2.16",
    install_type: installType,
    status: installType === "windows" ? "off" : "up_to_date",
    summary: "",
    latest_version: null,
    latest_name: null,
    published_at: null,
    release_url: null,
    windows_installer_url: null,
    docker_image: null,
    docker_tag: null,
    docker_update_command: null,
    in_app_upgrade_supported: installType === "windows",
    in_app_upgrade_summary: null,
  };
}

export function pendingSettings(over: Partial<AppSettings> = {}): AppSettings {
  return {
    setup_wizard_state: "pending",
    app_timezone: "UTC",
    log_retention_days: 30,
    activity_retention_days: 0,
    configuration_backup_enabled: false,
    configuration_backup_interval_hours: 24,
    configuration_backup_preferred_time: "02:00",
    configuration_backup_last_run_at: null,
    updated_at: "2026-04-23T00:00:00Z",
    ...over,
  };
}

/** A hang guard, not a pace: the most a screen is given to show what a request led to, on the fake clock; a poll ends the moment it does. */
export const CEILING = { timeout: 10_000 };

/** A test renders the whole wizard and waits on several requests, so it is given room well past its longest wait. */
export const TEST_CEILING_MS = 30_000;

const CSRF_TOKEN = "csrf-token";
const LIBRARY_PATH = /^\/api\/v1\/processing\/libraries(?:\/(\d+))?$/;

/** The folder check's answer for every media type whose folders were sent: nothing in the way. */
function passingChecks(body: Record<string, unknown>) {
  const chain = {
    library_id: 0,
    local: {
      ready: true,
      lines: [{ state: "ok", text: "Weir can read the folder." }],
    },
    managers: [],
    download_clients: [],
    ready: true,
  };
  const checked = (mediaType: string) =>
    `${mediaType}_watched_folder` in body
      ? { problem: null, chain }
      : undefined;
  return { movie: checked("movie"), tv: checked("tv") };
}

function jsonReply(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function bodyOf(init: RequestInit | undefined): Record<string, unknown> | null {
  if (typeof init?.body !== "string") return null;
  const { csrf_token: sent, ...body } = JSON.parse(init.body) as Record<
    string,
    unknown
  >;
  expectCsrf(sent);
  return body;
}

function expectCsrf(sent: unknown) {
  if (sent !== CSRF_TOKEN) {
    throw new Error("A change was sent without the token Weir handed out.");
  }
}

/**
 * Stands in for Weir's server at `fetch`, so the wizard's own hooks run and every request's URL, verb and
 * body can be read back. It keeps the libraries and settings it is given, and changes them as the real server
 * would: a POST adds a library, a PUT replaces one, a settings PUT merges.
 */
export class WizardServer {
  readonly state: WizardServerState;
  readonly sent: SentRequest[] = [];
  private nextLibraryId: number;

  constructor(over: Partial<WizardServerState> = {}) {
    this.state = {
      libraries: [],
      settings: pendingSettings(),
      managers: [],
      suggestions: { libraries: [], notes: [] },
      refuseSettingsSaves: 0,
      installType: "source",
      updateSettings: {
        mode: "Auto",
        check_on_startup: true,
        check_interval_minutes: 60,
        check_for_updates: false,
      },
      ...over,
    };
    this.nextLibraryId =
      Math.max(0, ...this.state.libraries.map((entry) => entry.id)) + 1;
    vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) =>
      this.answer(String(input), init),
    );
  }

  /** The requests that change something: every verb but GET, and the folder check, which only looks. */
  changes(): SentRequest[] {
    return this.sent.filter(
      (request) =>
        request.method !== "GET" &&
        request.path !== "/api/v1/processing/library-check",
    );
  }

  changesTo(method: string, pathPrefix: string): SentRequest[] {
    return this.changes().filter(
      (request) =>
        request.method === method && request.path.startsWith(pathPrefix),
    );
  }

  private answer(url: string, init: RequestInit | undefined) {
    const path = new URL(url, "http://localhost").pathname;
    const method = init?.method ?? "GET";
    const body = method === "GET" ? null : bodyOf(init);
    if (path !== "/api/v1/auth/csrf") this.sent.push({ method, path, body });
    return Promise.resolve(this.route(method, path, body));
  }

  private route(
    method: string,
    path: string,
    body: Record<string, unknown> | null,
  ): Response {
    const key = `${method} ${path}`;
    switch (key) {
      case "GET /api/v1/auth/csrf":
        return jsonReply(200, { csrf_token: CSRF_TOKEN });
      case "GET /api/v1/auth/me":
        return jsonReply(200, {
          user: { id: 1, username: "admin", role: "admin", app_theme: null },
        });
      case "GET /api/v1/suite/settings":
        return jsonReply(200, this.state.settings);
      case "PUT /api/v1/suite/settings":
        return this.saveSettings(body ?? {});
      case "GET /api/v1/suite/update-status":
        return jsonReply(200, updateStatusOf(this.state.installType));
      case "GET /api/v1/suite/update-settings":
        return jsonReply(200, this.state.updateSettings);
      case "PUT /api/v1/suite/update-settings":
        this.state.updateSettings = {
          ...this.state.updateSettings,
          ...body,
        } as UpdateSettingsOut;
        return jsonReply(200, this.state.updateSettings);
      case "GET /api/v1/media-managers/connections":
        return jsonReply(200, this.state.managers);
      case "GET /api/v1/processing/library-suggestions":
        return jsonReply(200, this.state.suggestions);
      case "POST /api/v1/processing/library-check":
        return jsonReply(200, passingChecks(body ?? {}));
    }
    const libraryRoute = LIBRARY_PATH.exec(path);
    if (libraryRoute) return this.libraryRoute(method, libraryRoute[1], body);
    throw new Error(`The wizard asked for ${key}, which this server lacks.`);
  }

  private saveSettings(body: Record<string, unknown>): Response {
    if (this.state.refuseSettingsSaves > 0) {
      this.state.refuseSettingsSaves -= 1;
      return jsonReply(500, { detail: "The settings could not be written." });
    }
    this.state.settings = { ...this.state.settings, ...body } as AppSettings;
    return jsonReply(200, this.state.settings);
  }

  private libraryRoute(
    method: string,
    id: string | undefined,
    body: Record<string, unknown> | null,
  ): Response {
    if (method === "GET" && id === undefined) {
      return jsonReply(200, this.state.libraries);
    }
    if (method === "POST" && id === undefined) {
      const created = library({
        ...(body as Partial<ProcessingLibrary>),
        id: this.nextLibraryId++,
        display_order: this.state.libraries.length,
      });
      this.state.libraries = [...this.state.libraries, created];
      return jsonReply(201, created);
    }
    if (method === "PUT" && id !== undefined) {
      const saved = this.state.libraries.find((entry) => entry.id === +id);
      if (!saved) return jsonReply(404, { detail: "No such workflow." });
      const replaced = { ...saved, ...(body as Partial<ProcessingLibrary>) };
      this.state.libraries = this.state.libraries.map((entry) =>
        entry.id === saved.id ? replaced : entry,
      );
      return jsonReply(200, replaced);
    }
    throw new Error(`The wizard sent ${method} to a workflow route.`);
  }
}

export function renderWizard() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: Infinity },
      mutations: { retry: false },
    },
  });
  const view = render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <SetupWizardPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return { client, ...view };
}

/**
 * Waits until the workflow list the wizard holds has been read again and drawn, so a second Finish sees what the
 * first one changed.
 */
export async function libraryListReread(client: QueryClient, count: number) {
  await waitFor(
    () =>
      expect(client.getQueryData(processingKeys.libraries)).toHaveLength(count),
    CEILING,
  );
  await act(async () => {});
}
