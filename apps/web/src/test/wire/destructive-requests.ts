import { forgetProcessingFile } from "../../lib/processing/files-api";
import {
  cleanLibraryFiles,
  saveLibrarySettings,
  setLibrarySchedule,
} from "../../lib/processing/library-mode-api";
import { requestLibraryRedownload } from "../../lib/processing/library-redownload-api";

/**
 * The stand-ins the requests below are built with. The server tests read the fixture these produce and put a real library, file,
 * path, size and time of the file in their place, so each is a value no real one is mistaken for.
 */
const SAMPLE = {
  libraryId: 7001,
  fileId: 7002,
  path: "{path}",
  libraryFolder: "{library_folder}",
  sizeBytes: 7003,
  modifiedAt: "{modified_at}",
} as const;

const LIBRARY = `/api/v1/processing/libraries/${SAMPLE.libraryId}`;
const FILE = `/api/v1/processing/files/${SAMPLE.fileId}`;

const AS_SHOWN = {
  size_bytes: SAMPLE.sizeBytes,
  modified_at: SAMPLE.modifiedAt,
};
const CONFIRM_AS_SHOWN = {
  confirm_size_bytes: SAMPLE.sizeBytes,
  confirm_modified_at: SAMPLE.modifiedAt,
};

/** Tracks 0 (video), 1 (English audio) and 3 (English subtitle) of a file that also has French audio as track 2. */
const KEEP_ENGLISH = {
  keep: [
    { index: 0, default: false, forced: false },
    { index: 1, default: true, forced: false },
    { index: 3, default: false, forced: false },
  ],
  order: [0, 1, 3],
  expected_size_bytes: SAMPLE.sizeBytes,
};

const CHOSEN_TRACKS = {
  manual_plan: { keep: KEEP_ENGLISH.keep, order: KEEP_ENGLISH.order },
  expected_size_bytes: SAMPLE.sizeBytes,
};

export type DestructiveRequest = {
  /** The name the server tests find it by. */
  name: string;
  call: () => Promise<unknown>;
  method: "POST" | "PUT" | "DELETE";
  path: string;
  /** The JSON body the web app must send, besides its token. */
  body: object;
};

/**
 * Every request the web app sends that can delete a file, remove a track for good, ask a media manager to delete one, or switch on a
 * clean that runs unattended, in each of the ways a person can send it.
 */
export const destructiveRequests: readonly DestructiveRequest[] = [
  {
    name: "remove-file/plain",
    call: () => forgetProcessingFile(SAMPLE.fileId),
    method: "DELETE",
    path: FILE,
    body: {},
  },
  {
    name: "remove-file/remove",
    call: () => forgetProcessingFile(SAMPLE.fileId, "remove"),
    method: "DELETE",
    path: FILE,
    body: { resolution: "remove" },
  },
  {
    name: "remove-file/remove-unrecorded",
    call: () => forgetProcessingFile(SAMPLE.fileId, "remove", AS_SHOWN),
    method: "DELETE",
    path: FILE,
    body: { resolution: "remove", ...CONFIRM_AS_SHOWN },
  },
  {
    name: "remove-file/delete",
    call: () => forgetProcessingFile(SAMPLE.fileId, "delete"),
    method: "DELETE",
    path: FILE,
    body: { resolution: "delete" },
  },
  {
    name: "remove-file/delete-unrecorded",
    call: () => forgetProcessingFile(SAMPLE.fileId, "delete", AS_SHOWN),
    method: "DELETE",
    path: FILE,
    body: { resolution: "delete", ...CONFIRM_AS_SHOWN },
  },
  {
    name: "remove-file/keep",
    call: () => forgetProcessingFile(SAMPLE.fileId, "keep"),
    method: "DELETE",
    path: FILE,
    body: { resolution: "keep" },
  },
  {
    name: "remove-file/keep-unrecorded",
    call: () => forgetProcessingFile(SAMPLE.fileId, "keep", AS_SHOWN),
    method: "DELETE",
    path: FILE,
    body: { resolution: "keep", ...CONFIRM_AS_SHOWN },
  },
  {
    name: "remove-file/retry",
    call: () => forgetProcessingFile(SAMPLE.fileId, "retry"),
    method: "DELETE",
    path: FILE,
    body: { resolution: "retry" },
  },
  {
    name: "remove-file/retry-unrecorded",
    call: () => forgetProcessingFile(SAMPLE.fileId, "retry", AS_SHOWN),
    method: "DELETE",
    path: FILE,
    body: { resolution: "retry", ...CONFIRM_AS_SHOWN },
  },
  {
    name: "clean/to-confirm",
    call: () => cleanLibraryFiles(SAMPLE.libraryId, [SAMPLE.path], false),
    method: "POST",
    path: `${LIBRARY}/library-files/clean`,
    body: { paths: [SAMPLE.path], confirm_final_removal: false },
  },
  {
    name: "clean/confirmed",
    call: () => cleanLibraryFiles(SAMPLE.libraryId, [SAMPLE.path], true),
    method: "POST",
    path: `${LIBRARY}/library-files/clean`,
    body: { paths: [SAMPLE.path], confirm_final_removal: true },
  },
  {
    name: "clean/chosen-tracks-to-confirm",
    call: () =>
      cleanLibraryFiles(SAMPLE.libraryId, [SAMPLE.path], false, KEEP_ENGLISH),
    method: "POST",
    path: `${LIBRARY}/library-files/clean`,
    body: {
      paths: [SAMPLE.path],
      confirm_final_removal: false,
      ...CHOSEN_TRACKS,
    },
  },
  {
    name: "clean/chosen-tracks-confirmed",
    call: () =>
      cleanLibraryFiles(SAMPLE.libraryId, [SAMPLE.path], true, KEEP_ENGLISH),
    method: "POST",
    path: `${LIBRARY}/library-files/clean`,
    body: {
      paths: [SAMPLE.path],
      confirm_final_removal: true,
      ...CHOSEN_TRACKS,
    },
  },
  {
    name: "schedule/on-to-confirm",
    call: () => setLibrarySchedule(SAMPLE.libraryId, true, false),
    method: "POST",
    path: `${LIBRARY}/library-schedule`,
    body: { enabled: true, confirm_final_removal: false },
  },
  {
    name: "schedule/on-confirmed",
    call: () => setLibrarySchedule(SAMPLE.libraryId, true, true),
    method: "POST",
    path: `${LIBRARY}/library-schedule`,
    body: { enabled: true, confirm_final_removal: true },
  },
  {
    name: "schedule/off",
    call: () => setLibrarySchedule(SAMPLE.libraryId, false, false),
    method: "POST",
    path: `${LIBRARY}/library-schedule`,
    body: { enabled: false, confirm_final_removal: false },
  },
  {
    name: "library-settings/save",
    call: () =>
      saveLibrarySettings(SAMPLE.libraryId, {
        library_folders: [SAMPLE.libraryFolder],
        clean_hardlinked_files: false,
        skip_if_manager_would_redownload: true,
        keep_original_after_clean: true,
        originals_folder: "",
        library_rule_set_id: null,
      }),
    method: "PUT",
    path: `${LIBRARY}/library-settings`,
    body: {
      library_folders: [SAMPLE.libraryFolder],
      clean_hardlinked_files: false,
      skip_if_manager_would_redownload: true,
      keep_original_after_clean: true,
      originals_folder: "",
      library_rule_set_id: null,
    },
  },
  {
    name: "download-again/confirmed",
    call: () => requestLibraryRedownload(SAMPLE.libraryId, SAMPLE.path),
    method: "POST",
    path: `${LIBRARY}/library-redownloads`,
    body: { path: SAMPLE.path, confirm_destructive: true },
  },
];
