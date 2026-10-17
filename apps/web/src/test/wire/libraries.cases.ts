import { recordedBody } from "../recorded";
import {
  writeFromProcessingRuleSet,
  type ProcessingRuleSet,
} from "../../lib/processing/rule-sets-api";
import {
  createProcessingLibrary,
  deleteProcessingLibrary,
  fetchProcessingLibraries,
  reorderProcessingLibraries,
  updateProcessingLibrary,
  writeFromProcessingLibrary,
  type ProcessingLibrary,
} from "../../lib/processing/libraries-api";
import {
  cleanLibraryFiles,
  fetchLibraryFiles,
  fetchLibraryOverview,
  fetchLibrarySettings,
  saveLibrarySettings,
  setLibraryFileLeaveAlone,
  setLibrarySchedule,
  triggerLibraryScan,
  type LibraryConfirmationRequired,
  type LibraryFilesResult,
} from "../../lib/processing/library-mode-api";
import {
  discoverProcessingLibraries,
  fetchProcessingLibraryDrift,
  fetchProcessingManagerSetup,
  fetchProcessingRejectSupport,
  importDiscoveredProcessingLibraries,
  unlinkDiscoveredProcessingLibrary,
} from "../../lib/processing/library-managers-api";
import {
  fetchLibraryRedownloads,
  requestLibraryRedownload,
} from "../../lib/processing/library-redownload-api";
import {
  checkProposedLibraries,
  fetchLibrarySuggestions,
} from "../../lib/processing/library-setup-api";
import {
  fetchConnectionFolderChain,
  fetchLibraryFolderChain,
} from "../../lib/processing/library-folder-chain-api";
import { previewProcessingRules } from "../../lib/processing/rules-preview-api";
import type { RequestCase } from "./request-case";

const LIBRARIES = "/api/v1/processing/libraries";
const WORKFLOWS = recordedBody<ProcessingLibrary[]>(`GET ${LIBRARIES}`);
const LIBRARY_FILE = recordedBody<LibraryFilesResult>(
  `GET ${LIBRARIES}/1/library-files`,
).files[0]!.path;
const WATCHED = WORKFLOWS[0]!.watched_folder;
const OUTPUT = WORKFLOWS[0]!.output_folder;
const LIBRARY_FOLDER = recordedBody<{ library_folders: string[] }>(
  `GET ${LIBRARIES}/1/library-settings`,
).library_folders[0]!;
const PROFILE = writeFromProcessingRuleSet(
  recordedBody<ProcessingRuleSet[]>("GET /api/v1/processing/rule-sets")[0]!,
);
const CONFIRMATION = recordedBody<{
  detail: string;
  files_count: number;
  tracks_count: number;
  estimated_bytes_saved: number;
  warnings: string[];
}>(`POST ${LIBRARIES}/1/library-files/clean (to confirm)`);

/** The requests the Workflows settings make, and the ones behind a workflow's folders and its library. */
export const workflowRequestCases: RequestCase[] = [
  {
    fn: fetchProcessingLibraries,
    asking: "every workflow",
    call: () => fetchProcessingLibraries(),
    method: "GET",
    path: LIBRARIES,
    reply: `GET ${LIBRARIES}`,
  },
  {
    fn: createProcessingLibrary,
    asking: "a new workflow with only a name and a kind of media",
    call: () =>
      createProcessingLibrary({ name: "Recorded extra", media_type: "movie" }),
    method: "POST",
    path: LIBRARIES,
    body: { name: "Recorded extra", media_type: "movie" },
    reply: `POST ${LIBRARIES}`,
  },
  {
    fn: updateProcessingLibrary,
    asking: "saving a workflow with everything it has",
    call: () =>
      updateProcessingLibrary(1, writeFromProcessingLibrary(WORKFLOWS[0]!)),
    method: "PUT",
    path: `${LIBRARIES}/1`,
    body: writeFromProcessingLibrary(WORKFLOWS[0]!),
    reply: `PUT ${LIBRARIES}/1`,
  },
  {
    fn: deleteProcessingLibrary,
    asking: "removing a workflow",
    call: () => deleteProcessingLibrary(3),
    method: "DELETE",
    path: `${LIBRARIES}/3`,
    body: {},
    reply: `DELETE ${LIBRARIES}/3`,
  },
  {
    fn: reorderProcessingLibraries,
    asking: "putting the workflows in a new order",
    call: () => reorderProcessingLibraries([3, 1, 2]),
    method: "POST",
    path: `${LIBRARIES}/reorder`,
    body: { library_ids_in_order: [3, 1, 2] },
    reply: `POST ${LIBRARIES}/reorder`,
  },
  {
    fn: unlinkDiscoveredProcessingLibrary,
    asking: "unlinking a workflow from the manager it came from",
    call: () => unlinkDiscoveredProcessingLibrary(3),
    method: "POST",
    path: `${LIBRARIES}/3/unlink`,
    body: {},
    reply: `POST ${LIBRARIES}/3/unlink`,
  },
  {
    fn: discoverProcessingLibraries,
    asking: "the libraries a manager that cannot be reached would offer",
    call: () => discoverProcessingLibraries(1),
    method: "GET",
    path: `${LIBRARIES}/discover/1`,
    reply: `GET ${LIBRARIES}/discover/1`,
  },
  {
    fn: importDiscoveredProcessingLibraries,
    asking: "importing libraries from a manager that cannot be reached",
    call: () => importDiscoveredProcessingLibraries(1, ["movies"]),
    method: "POST",
    path: `${LIBRARIES}/discover/1/import`,
    body: { keys: ["movies"] },
    reply: `POST ${LIBRARIES}/discover/1/import`,
  },
  {
    fn: fetchProcessingLibraryDrift,
    asking: "how the workflows differ from a manager that cannot be reached",
    call: () => fetchProcessingLibraryDrift(1),
    method: "GET",
    path: `${LIBRARIES}/discover/1/drift`,
    reply: `GET ${LIBRARIES}/discover/1/drift`,
  },
  {
    fn: fetchProcessingRejectSupport,
    asking: "whether Reject can be chosen, with no manager linked",
    call: () => fetchProcessingRejectSupport([]),
    method: "GET",
    path: "/api/v1/processing/reject-support",
    reply: "GET /api/v1/processing/reject-support",
  },
  {
    fn: fetchProcessingRejectSupport,
    asking: "whether Reject can be chosen, for one linked manager",
    call: () => fetchProcessingRejectSupport([1]),
    method: "GET",
    path: "/api/v1/processing/reject-support",
    query: [["connection_ids", "1"]],
    reply: "GET /api/v1/processing/reject-support?connection_ids=1",
  },
  {
    fn: fetchProcessingRejectSupport,
    asking: "whether Reject can be chosen, with every linked manager repeated",
    call: () => fetchProcessingRejectSupport([1, 2]),
    method: "GET",
    path: "/api/v1/processing/reject-support",
    query: [
      ["connection_ids", "1"],
      ["connection_ids", "2"],
    ],
    reply:
      "GET /api/v1/processing/reject-support?connection_ids=1&connection_ids=2",
  },
  {
    fn: fetchProcessingManagerSetup,
    asking: "what the managers need for these folders, keeping the original",
    call: () => fetchProcessingManagerSetup("movie", WATCHED, OUTPUT),
    method: "GET",
    path: "/api/v1/processing/manager-setup",
    query: [
      ["media_type", "movie"],
      ["watched_folder", WATCHED],
      ["output_folder", OUTPUT],
      ["remove_original_after_success", "true"],
    ],
    reply: `GET /api/v1/processing/manager-setup?media_type=movie&watched_folder=${encodeURIComponent(WATCHED)}&output_folder=${encodeURIComponent(OUTPUT)}&remove_original_after_success=true`,
  },
  {
    fn: fetchProcessingManagerSetup,
    asking: "what the linked managers need, when the original stays",
    call: () =>
      fetchProcessingManagerSetup("tv", WATCHED, OUTPUT, false, [1, 2]),
    method: "GET",
    path: "/api/v1/processing/manager-setup",
    query: [
      ["media_type", "tv"],
      ["watched_folder", WATCHED],
      ["output_folder", OUTPUT],
      ["remove_original_after_success", "false"],
      ["connection_ids", "1"],
      ["connection_ids", "2"],
    ],
    reply: `GET /api/v1/processing/manager-setup?media_type=tv&watched_folder=${encodeURIComponent(WATCHED)}&output_folder=${encodeURIComponent(OUTPUT)}&remove_original_after_success=false&connection_ids=1&connection_ids=2`,
  },
  {
    fn: fetchLibraryFolderChain,
    asking: "the folder chain of a workflow",
    call: () => fetchLibraryFolderChain(1),
    method: "GET",
    path: `${LIBRARIES}/1/folder-chain`,
    reply: `GET ${LIBRARIES}/1/folder-chain`,
  },
  {
    fn: fetchConnectionFolderChain,
    asking: "the folder chains of the workflows linked to a manager",
    call: () => fetchConnectionFolderChain(1),
    method: "GET",
    path: "/api/v1/media-managers/connections/1/folder-chain",
    reply: "GET /api/v1/media-managers/connections/1/folder-chain",
  },
  {
    fn: fetchLibrarySuggestions,
    asking: "the folders the connections suggest",
    call: () => fetchLibrarySuggestions(),
    method: "GET",
    path: "/api/v1/processing/library-suggestions",
    reply: "GET /api/v1/processing/library-suggestions",
  },
  {
    fn: checkProposedLibraries,
    asking: "what workflows with these folders would run into",
    call: () =>
      checkProposedLibraries({ movie: { watched: WATCHED, output: OUTPUT } }),
    method: "POST",
    path: "/api/v1/processing/library-check",
    body: { movie_watched_folder: WATCHED, movie_output_folder: OUTPUT },
    reply: "POST /api/v1/processing/library-check",
    result: {
      movie: recordedBody<{ movie: object }>(
        "POST /api/v1/processing/library-check",
      ).movie,
    },
  },
  {
    fn: previewProcessingRules,
    asking: "the rules on a file given by its full path",
    call: () =>
      previewProcessingRules({ libraryId: 1, absolutePath: LIBRARY_FILE }),
    method: "POST",
    path: `${LIBRARIES}/1/preview`,
    body: { absolute_path: LIBRARY_FILE },
    reply: `POST ${LIBRARIES}/1/preview (full path)`,
  },
  {
    fn: previewProcessingRules,
    asking:
      "the rules on a file in the watched folder, with a saved profile and unsaved edits",
    call: () =>
      previewProcessingRules({
        libraryId: 1,
        relativePath: "Recorded Film (2020).mkv",
        ruleSetId: 3,
        rules: PROFILE,
      }),
    method: "POST",
    path: `${LIBRARIES}/1/preview`,
    body: {
      relative_path: "Recorded Film (2020).mkv",
      rule_set_id: 3,
      rules: PROFILE,
    },
    reply: `POST ${LIBRARIES}/1/preview (in the watched folder)`,
  },
  {
    fn: fetchLibrarySettings,
    asking: "a workflow's library setup",
    call: () => fetchLibrarySettings(1),
    method: "GET",
    path: `${LIBRARIES}/1/library-settings`,
    reply: `GET ${LIBRARIES}/1/library-settings`,
  },
  {
    fn: saveLibrarySettings,
    asking: "saving a workflow's library setup",
    call: () =>
      saveLibrarySettings(1, {
        library_folders: [LIBRARY_FOLDER],
        keep_original_after_clean: true,
        originals_folder: "kept",
        library_rule_set_id: null,
      }),
    method: "PUT",
    path: `${LIBRARIES}/1/library-settings`,
    body: {
      library_folders: [LIBRARY_FOLDER],
      keep_original_after_clean: true,
      originals_folder: "kept",
      library_rule_set_id: null,
    },
    reply: `PUT ${LIBRARIES}/1/library-settings`,
  },
  {
    fn: triggerLibraryScan,
    asking: "scanning a workflow's library now",
    call: () => triggerLibraryScan(1),
    method: "POST",
    path: `${LIBRARIES}/1/library-scan`,
    body: {},
    reply: `POST ${LIBRARIES}/1/library-scan`,
  },
  {
    fn: fetchLibraryOverview,
    asking: "a library's overview",
    call: () => fetchLibraryOverview(1),
    method: "GET",
    path: `${LIBRARIES}/1/library-overview`,
    reply: `GET ${LIBRARIES}/1/library-overview`,
  },
  {
    fn: fetchLibraryFiles,
    asking: "a library's files, with no filter",
    call: () => fetchLibraryFiles(1),
    method: "GET",
    path: `${LIBRARIES}/1/library-files`,
    reply: `GET ${LIBRARIES}/1/library-files`,
  },
  {
    fn: fetchLibraryFiles,
    asking: "a library's files, narrowed, sorted and paged",
    call: () =>
      fetchLibraryFiles(1, {
        status: "needs_cleaning",
        q: "Library",
        sort: "size",
        direction: "desc",
        page: 1,
        page_size: 25,
      }),
    method: "GET",
    path: `${LIBRARIES}/1/library-files`,
    query: [
      ["status", "needs_cleaning"],
      ["q", "Library"],
      ["sort", "size"],
      ["direction", "desc"],
      ["page_size", "25"],
    ],
    reply: `GET ${LIBRARIES}/1/library-files?status=needs_cleaning&q=Library&sort=size&direction=desc&page_size=25`,
  },
  {
    fn: cleanLibraryFiles,
    asking: "cleaning a file before it is confirmed",
    call: () => cleanLibraryFiles(1, [LIBRARY_FILE], false),
    method: "POST",
    path: `${LIBRARIES}/1/library-files/clean`,
    body: { paths: [LIBRARY_FILE], confirm_final_removal: false },
    reply: `POST ${LIBRARIES}/1/library-files/clean (to confirm)`,
    handlesRefusal: true,
    result: {
      kind: "confirmation_required",
      detail: CONFIRMATION.detail,
      files_count: CONFIRMATION.files_count,
      tracks_count: CONFIRMATION.tracks_count,
      estimated_bytes_saved: CONFIRMATION.estimated_bytes_saved,
      warnings: CONFIRMATION.warnings,
    } satisfies LibraryConfirmationRequired,
  },
  {
    fn: cleanLibraryFiles,
    asking: "cleaning a file once it is confirmed",
    call: () => cleanLibraryFiles(1, [LIBRARY_FILE], true),
    method: "POST",
    path: `${LIBRARIES}/1/library-files/clean`,
    body: { paths: [LIBRARY_FILE], confirm_final_removal: true },
    reply: `POST ${LIBRARIES}/1/library-files/clean`,
    result: {
      kind: "cleaned",
      ...recordedBody<object>(`POST ${LIBRARIES}/1/library-files/clean`),
    },
  },
  {
    fn: setLibraryFileLeaveAlone,
    asking: "leaving a file alone",
    call: () => setLibraryFileLeaveAlone(1, LIBRARY_FILE, true),
    method: "POST",
    path: `${LIBRARIES}/1/library-files/leave-alone`,
    body: { path: LIBRARY_FILE, leave_alone: true },
    reply: `POST ${LIBRARIES}/1/library-files/leave-alone`,
  },
  {
    fn: setLibraryFileLeaveAlone,
    asking: "taking a file back from being left alone",
    call: () => setLibraryFileLeaveAlone(1, LIBRARY_FILE, false),
    method: "POST",
    path: `${LIBRARIES}/1/library-files/leave-alone`,
    body: { path: LIBRARY_FILE, leave_alone: false },
    reply: `POST ${LIBRARIES}/1/library-files/leave-alone (undone)`,
  },
  {
    fn: setLibrarySchedule,
    asking: "turning a library's schedule off",
    call: () => setLibrarySchedule(1, false, false),
    method: "POST",
    path: `${LIBRARIES}/1/library-schedule`,
    body: { enabled: false, confirm_final_removal: false },
    reply: `POST ${LIBRARIES}/1/library-schedule`,
  },
  {
    fn: fetchLibraryRedownloads,
    asking: "the files missing tracks the rules now keep",
    call: () => fetchLibraryRedownloads(1),
    method: "GET",
    path: `${LIBRARIES}/1/library-redownloads`,
    reply: `GET ${LIBRARIES}/1/library-redownloads`,
  },
  {
    fn: requestLibraryRedownload,
    asking: "downloading a file again, once confirmed",
    call: () => requestLibraryRedownload(1, LIBRARY_FILE),
    method: "POST",
    path: `${LIBRARIES}/1/library-redownloads`,
    body: { path: LIBRARY_FILE, confirm_destructive: true },
    reply: `POST ${LIBRARIES}/1/library-redownloads`,
  },
];
