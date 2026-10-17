import { fetchActivityRecent } from "../../lib/api/activity-api";
import {
  fetchProcessingFileLog,
  fetchProcessingFileRemoveOptions,
  fetchProcessingFiles,
  fetchProcessingFileTracks,
  fetchProcessingWhyHeld,
  forgetProcessingFile,
  moveProcessingFileToTop,
  postProcessingManualPlan,
  requeueProcessingFile,
  requeueProcessingFiles,
} from "../../lib/processing/files-api";
import { fetchProcessingFilesAtOnce } from "../../lib/processing/files-at-once-api";
import { postProcessingFileRemuxPassEnqueue } from "../../lib/processing/file-remux-pass-api";
import {
  fetchKeptFiles,
  processKeptFileAgain,
} from "../../lib/processing/kept-files-api";
import { fetchLibraryCleans } from "../../lib/processing/library-cleans-api";
import {
  fetchRejectedFilesSummary,
  processRejectedFilesAgain,
} from "../../lib/processing/rejected-files-api";
import { postProcessingWatchedFolderRemuxScanDispatchEnqueue } from "../../lib/processing/watched-folder-scan-api";
import type { RequestCase } from "./request-case";

const FILES = "/api/v1/processing/files";

/** The requests Activity makes: what happened, the files in the list, and what a person can do to one. */
export const activityRequestCases: RequestCase[] = [
  {
    fn: fetchActivityRecent,
    asking: "the newest events, with every filter",
    call: () =>
      fetchActivityRecent({
        limit: 20,
        library_id: 1,
        result: "success",
        about: "files",
        current_only: true,
        with_total: false,
      }),
    method: "GET",
    path: "/api/v1/activity/recent",
    query: [
      ["result", "success"],
      ["library_id", "1"],
      ["about", "files"],
      ["limit", "20"],
      ["current_only", "true"],
      ["with_total", "false"],
    ],
    reply:
      "GET /api/v1/activity/recent?result=success&library_id=1&about=files&limit=20&current_only=true&with_total=false",
  },
  {
    fn: fetchActivityRecent,
    asking: "the newest events, with no filter",
    call: () => fetchActivityRecent({ limit: 10 }),
    method: "GET",
    path: "/api/v1/activity/recent",
    query: [["limit", "10"]],
    reply: "GET /api/v1/activity/recent?limit=10",
  },
  {
    fn: fetchProcessingFiles,
    asking: "every file, with no filter",
    call: () => fetchProcessingFiles(),
    method: "GET",
    path: FILES,
    reply: "GET /api/v1/processing/files",
  },
  {
    fn: fetchProcessingFiles,
    asking: "the files of one workflow, by status, name, age and order",
    call: () =>
      fetchProcessingFiles({
        library_id: 1,
        file_status: "processed",
        path_contains: "Recorded",
        within_days: 30,
        limit: 50,
        sort: "when",
        direction: "desc",
      }),
    method: "GET",
    path: FILES,
    query: [
      ["library_id", "1"],
      ["file_status", "processed"],
      ["path_contains", "Recorded"],
      ["within_days", "30"],
      ["limit", "50"],
      ["sort", "when"],
      ["direction", "desc"],
    ],
    reply:
      "GET /api/v1/processing/files?library_id=1&file_status=processed&path_contains=Recorded&within_days=30&limit=50&sort=when&direction=desc",
  },
  {
    fn: fetchProcessingFiles,
    asking:
      "several statuses as one comma-separated value, leaving out a blank search",
    call: () =>
      fetchProcessingFiles({
        file_status: ["processed", "skipped"],
        path_contains: "",
      }),
    method: "GET",
    path: FILES,
    query: [["file_status", "processed,skipped"]],
    reply: "GET /api/v1/processing/files",
  },
  {
    fn: fetchProcessingFileRemoveOptions,
    asking: "what removing a file would offer",
    call: () => fetchProcessingFileRemoveOptions(1),
    method: "GET",
    path: `${FILES}/1/remove-options`,
    reply: "GET /api/v1/processing/files/1/remove-options",
  },
  {
    fn: forgetProcessingFile,
    asking: "removing a file from the list, with nothing else said",
    call: () => forgetProcessingFile(1),
    method: "DELETE",
    path: `${FILES}/1`,
    body: {},
  },
  {
    fn: forgetProcessingFile,
    asking: "trying a file again, with the file as the dialog showed it",
    call: () =>
      forgetProcessingFile(1, "retry", {
        size_bytes: 22808,
        modified_at: "2026-10-15T18:50:00",
      }),
    method: "DELETE",
    path: `${FILES}/1`,
    body: {
      resolution: "retry",
      confirm_size_bytes: 22808,
      confirm_modified_at: "2026-10-15T18:50:00",
    },
    reply: "DELETE /api/v1/processing/files/1 (try again)",
  },
  {
    fn: moveProcessingFileToTop,
    asking: "moving a file to the front of the queue",
    call: () => moveProcessingFileToTop(1),
    method: "POST",
    path: `${FILES}/1/move-to-top`,
    body: {},
    reply: "POST /api/v1/processing/files/1/move-to-top",
  },
  {
    fn: requeueProcessingFile,
    asking: "queuing one file again",
    call: () => requeueProcessingFile(1),
    method: "POST",
    path: `${FILES}/1/requeue`,
    body: {},
    reply: "POST /api/v1/processing/files/1/requeue",
  },
  {
    fn: requeueProcessingFiles,
    asking: "queuing exactly the files on screen again",
    call: () => requeueProcessingFiles({ file_ids: [1] }),
    method: "POST",
    path: `${FILES}/requeue`,
    body: { file_ids: [1] },
    reply: "POST /api/v1/processing/files/requeue",
  },
  {
    fn: fetchProcessingWhyHeld,
    asking: "why a file is held",
    call: () => fetchProcessingWhyHeld(1),
    method: "GET",
    path: `${FILES}/1/why-held`,
    reply: "GET /api/v1/processing/files/1/why-held",
  },
  {
    fn: fetchProcessingFileLog,
    asking: "what happened to a file",
    call: () => fetchProcessingFileLog(1),
    method: "GET",
    path: `${FILES}/1/log`,
    reply: "GET /api/v1/processing/files/1/log",
  },
  {
    fn: fetchProcessingFileTracks,
    asking: "the tracks of a held file",
    call: () => fetchProcessingFileTracks(1),
    method: "GET",
    path: `${FILES}/1/tracks`,
    reply: "GET /api/v1/processing/files/1/tracks",
  },
  {
    fn: postProcessingManualPlan,
    asking:
      "a manual choice of tracks, which the server refuses for a file that is not held",
    call: () =>
      postProcessingManualPlan(1, {
        keep: [{ index: 1, default: true, forced: false }],
        order: [1],
      }),
    method: "POST",
    path: `${FILES}/1/manual-plan`,
    body: { keep: [{ index: 1, default: true, forced: false }], order: [1] },
    reply: "POST /api/v1/processing/files/1/manual-plan",
  },
  {
    fn: fetchRejectedFilesSummary,
    asking: "the rejected files of every workflow",
    call: () => fetchRejectedFilesSummary(),
    method: "GET",
    path: `${FILES}/rejected/summary`,
    reply: "GET /api/v1/processing/files/rejected/summary",
  },
  {
    fn: fetchRejectedFilesSummary,
    asking: "the rejected files of one workflow",
    call: () => fetchRejectedFilesSummary(1),
    method: "GET",
    path: `${FILES}/rejected/summary`,
    query: [["library_id", "1"]],
    reply: "GET /api/v1/processing/files/rejected/summary?library_id=1",
  },
  {
    fn: processRejectedFilesAgain,
    asking: "processing one workflow's rejected files again",
    call: () => processRejectedFilesAgain(1),
    method: "POST",
    path: `${FILES}/rejected/process-again`,
    body: { library_id: 1 },
    reply: "POST /api/v1/processing/files/rejected/process-again",
  },
  {
    fn: fetchKeptFiles,
    asking: "the files kept without processing",
    call: () => fetchKeptFiles(),
    method: "GET",
    path: "/api/v1/processing/kept-files",
    reply: "GET /api/v1/processing/kept-files",
  },
  {
    fn: processKeptFileAgain,
    asking:
      "processing a kept file again, which the server refuses for a file it does not keep",
    call: () => processKeptFileAgain(9999),
    method: "POST",
    path: "/api/v1/processing/kept-files/9999/process-again",
    body: {},
    reply: "POST /api/v1/processing/kept-files/9999/process-again",
  },
  {
    fn: fetchLibraryCleans,
    asking: "the library cleans, with no filter",
    call: () => fetchLibraryCleans({}),
    method: "GET",
    path: "/api/v1/processing/library-cleans",
    reply: "GET /api/v1/processing/library-cleans",
  },
  {
    fn: fetchLibraryCleans,
    asking: "the library cleans of one workflow, by name and age",
    call: () =>
      fetchLibraryCleans({
        library_id: 1,
        path_contains: "Library",
        within_days: 7,
        limit: 20,
      }),
    method: "GET",
    path: "/api/v1/processing/library-cleans",
    query: [
      ["library_id", "1"],
      ["path_contains", "Library"],
      ["within_days", "7"],
      ["limit", "20"],
    ],
    reply:
      "GET /api/v1/processing/library-cleans?library_id=1&path_contains=Library&within_days=7&limit=20",
  },
  {
    fn: fetchProcessingFilesAtOnce,
    asking: "what is running and what is waiting",
    call: () => fetchProcessingFilesAtOnce(),
    method: "GET",
    path: "/api/v1/processing/files-at-once",
    reply: "GET /api/v1/processing/files-at-once",
  },
  {
    fn: postProcessingWatchedFolderRemuxScanDispatchEnqueue,
    asking: "looking at the watched folder now",
    call: () =>
      postProcessingWatchedFolderRemuxScanDispatchEnqueue({
        enqueue_remux_jobs: true,
        media_scope: "movie",
        library_id: 1,
      }),
    method: "POST",
    path: "/api/v1/processing/jobs/watched-folder-remux-scan-dispatch/enqueue",
    body: { enqueue_remux_jobs: true, media_scope: "movie", library_id: 1 },
    reply:
      "POST /api/v1/processing/jobs/watched-folder-remux-scan-dispatch/enqueue",
  },
  {
    fn: postProcessingFileRemuxPassEnqueue,
    asking: "a pass over one file",
    call: () =>
      postProcessingFileRemuxPassEnqueue({
        relative_media_path: "Recorded Film (2020).mkv",
        media_scope: "movie",
        library_id: 1,
      }),
    method: "POST",
    path: "/api/v1/processing/jobs/file-remux-pass/enqueue",
    body: {
      relative_media_path: "Recorded Film (2020).mkv",
      media_scope: "movie",
      library_id: 1,
    },
    reply: "POST /api/v1/processing/jobs/file-remux-pass/enqueue",
  },
];
