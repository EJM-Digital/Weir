import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { vi } from "vitest";

import { FOLDER_CHECK_SETTLE_MS } from "./folder-check-settle";
import * as authQueries from "../../../../lib/auth/queries";
import * as downloadClientsApi from "../../../../lib/download-clients/download-clients-api";
import * as managerApi from "../../../../lib/media-managers/media-managers-api";
import type { ProcessingLibrary } from "../../../../lib/processing/libraries-api";
import * as chainApi from "../../../../lib/processing/library-folder-chain-api";
import * as managersApi from "../../../../lib/processing/library-managers-api";
import * as operatorApi from "../../../../lib/processing/operator-settings-api";
import * as ruleSetsApi from "../../../../lib/processing/rule-sets-api";

/** What the Libraries tab's tests share: a saved library, the providers, and an operator's session. */
export function library(
  over: Partial<ProcessingLibrary> = {},
): ProcessingLibrary {
  return {
    id: 1,
    name: "Movies",
    enabled: true,
    media_type: "movie",
    display_order: 0,
    watched_folder: "/srv/movies/in",
    work_folder: "",
    output_folder: "/srv/movies/out",
    media_extensions_csv: ".mkv,.mp4",
    exclude_markers_csv: "__admin__",
    include_patterns_csv: "",
    exclude_patterns_csv: "",
    min_file_size_mb: 50,
    max_file_size_mb: 0,
    rejected_file_action: "leave",
    ready_after_seconds: 60,
    created_after: null,
    created_before: null,
    modified_after: null,
    modified_before: null,
    exclude_hidden: true,
    top_level_only: false,
    scan_interval_seconds: 300,
    sidecar_patterns_csv: ".srt,.nfo",
    preserve_original_timestamps: false,
    remove_original_after_success: true,
    minimum_free_disk_space_mb: 5120,
    remux_writer: "best",
    output_collision_policy: "replace",
    ffmpeg_strictness: "normal",
    ignore_size_changes: false,
    skip_access_tests: false,
    file_system_events_enabled: true,
    max_attempts: 3,
    retry_backoff_seconds: 300,
    retry_execution_failures: true,
    retry_preflight_failures: false,
    failure_policy: "pass_through",
    schedule_grid: "",
    schedule_enabled: true,
    schedule_hours_limited: false,
    schedule_days: "",
    schedule_start: "00:00",
    schedule_end: "23:59",
    max_concurrent_files: 1,
    effective_max_concurrent_files: 1,
    priority: 0,
    rule_set_id: null,
    manager_connection_ids: [],
    manager_coverage: "no_upstream_signal",
    manager_coverage_detail:
      "No media manager has been tested for this workflow.",
    discovered_from_connection_id: null,
    discovered_library_key: null,
    folders_synced_from_connection_id: null,
    active_job_count: 0,
    updated_at: null,
    ...over,
  };
}

export function wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return (
    <MemoryRouter>
      <QueryClientProvider client={qc}>{children}</QueryClientProvider>
    </MemoryRouter>
  );
}

function asRole(role: "operator" | "viewer") {
  vi.spyOn(authQueries, "useMeQuery").mockReturnValue({
    data: { role },
  } as ReturnType<typeof authQueries.useMeQuery>);
  vi.spyOn(managerApi, "fetchMediaManagerConnections").mockResolvedValue([]);
  vi.spyOn(ruleSetsApi, "fetchProcessingRuleSets").mockResolvedValue([]);
  answerEditorChecksAsUnavailable();
}

export function asOperator() {
  asRole("operator");
}

export function asViewer() {
  asRole("viewer");
}

/**
 * The library editor also asks about folders, managers and performance settings. A test that is
 * not about one of those gets "unavailable" for it, and one that is stubs it after this.
 */
function answerEditorChecksAsUnavailable() {
  const unavailable = () =>
    Promise.reject(new Error("Not stubbed by this test."));
  vi.spyOn(chainApi, "fetchLibraryFolderChain").mockImplementation(unavailable);
  vi.spyOn(managersApi, "fetchProcessingManagerSetup").mockImplementation(
    unavailable,
  );
  vi.spyOn(managersApi, "fetchProcessingRejectSupport").mockImplementation(
    unavailable,
  );
  vi.spyOn(
    downloadClientsApi,
    "fetchDownloadClientSuggestions",
  ).mockImplementation(unavailable);
  vi.spyOn(operatorApi, "fetchProcessingOperatorSettings").mockImplementation(
    unavailable,
  );
}

/**
 * Lets the folders a test rendered settle so their check starts, without waiting for it in real
 * time. The test must have installed fake timers with `useFakeClock()`.
 */
export async function settleFolderChecks() {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(FOLDER_CHECK_SETTLE_MS);
  });
}
