import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiHttpError } from "../api/client";
import { recordedBody } from "../../test/recorded";
import { unexpectedProblem } from "../../test/stub-weir-api";
import { stubWire } from "../../test/wire";
import {
  createProcessingLibrary,
  deleteProcessingLibrary,
  fetchProcessingLibraries,
  reorderProcessingLibraries,
  updateProcessingLibrary,
  writeFromProcessingLibrary,
  type ProcessingLibrary,
} from "./libraries-api";
import { fetchLibraryCleans } from "./library-cleans-api";
import {
  fetchConnectionFolderChain,
  fetchLibraryFolderChain,
} from "./library-folder-chain-api";
import {
  discoverProcessingLibraries,
  fetchProcessingLibraryDrift,
  fetchProcessingManagerSetup,
  fetchProcessingRejectSupport,
  importDiscoveredProcessingLibraries,
  unlinkDiscoveredProcessingLibrary,
} from "./library-managers-api";
import {
  fetchLibraryFiles,
  fetchLibraryOverview,
  fetchLibrarySettings,
  saveLibrarySettings,
  setLibraryFileLeaveAlone,
  setLibrarySchedule,
  triggerLibraryScan,
} from "./library-mode-api";
import {
  fetchLibraryRedownloads,
  requestLibraryRedownload,
} from "./library-redownload-api";
import {
  checkProposedLibraries,
  fetchLibrarySuggestions,
} from "./library-setup-api";
import {
  createProcessingRuleSet,
  deleteProcessingRuleSet,
  fetchProcessingRuleSets,
  updateProcessingRuleSet,
  type ProcessingRuleSetWrite,
} from "./rule-sets-api";

const LIBRARIES = "/api/v1/processing/libraries";
const RULE_SETS = "/api/v1/processing/rule-sets";
const WORKFLOW = recordedBody<ProcessingLibrary[]>(`GET ${LIBRARIES}`)[0]!;
const NO_RULES = {} as ProcessingRuleSetWrite;

type Failing = {
  /** The request that fails, as `METHOD /path`. */
  route: string;
  call: () => Promise<unknown>;
  /** What a person reads when the server gave no reason of its own. */
  says: string;
};

const failures: Failing[] = [
  {
    route: `GET ${LIBRARIES}`,
    call: () => fetchProcessingLibraries(),
    says: "Could not load workflows.",
  },
  {
    route: `POST ${LIBRARIES}`,
    call: () => createProcessingLibrary({ name: "A", media_type: "movie" }),
    says: "Could not add that workflow.",
  },
  {
    route: `PUT ${LIBRARIES}/1`,
    call: () =>
      updateProcessingLibrary(1, writeFromProcessingLibrary(WORKFLOW)),
    says: "Could not save that workflow.",
  },
  {
    route: `DELETE ${LIBRARIES}/3`,
    call: () => deleteProcessingLibrary(3),
    says: "Could not remove that workflow.",
  },
  {
    route: `POST ${LIBRARIES}/reorder`,
    call: () => reorderProcessingLibraries([2, 1]),
    says: "Could not reorder workflows.",
  },
  {
    route: "GET /api/v1/processing/library-cleans",
    call: () => fetchLibraryCleans({}),
    says: "Could not load library cleans.",
  },
  {
    route: `GET ${LIBRARIES}/1/folder-chain`,
    call: () => fetchLibraryFolderChain(1),
    says: "Could not check this workflow's folder chain.",
  },
  {
    route: "GET /api/v1/media-managers/connections/1/folder-chain",
    call: () => fetchConnectionFolderChain(1),
    says: "Could not check the folder chain of the workflows linked to it.",
  },
  {
    route: `GET ${LIBRARIES}/discover/1`,
    call: () => discoverProcessingLibraries(1),
    says: "Could not ask that media manager for its libraries.",
  },
  {
    route: `POST ${LIBRARIES}/discover/1/import`,
    call: () => importDiscoveredProcessingLibraries(1, ["movies"]),
    says: "Could not import those libraries.",
  },
  {
    route: `GET ${LIBRARIES}/discover/1/drift`,
    call: () => fetchProcessingLibraryDrift(1),
    says: "Could not compare workflows with that media manager.",
  },
  {
    route: `POST ${LIBRARIES}/3/unlink`,
    call: () => unlinkDiscoveredProcessingLibrary(3),
    says: "Could not unlink that workflow.",
  },
  {
    route: "GET /api/v1/processing/reject-support",
    call: () => fetchProcessingRejectSupport([]),
    says: "Could not check whether Reject is available.",
  },
  {
    route: "GET /api/v1/processing/manager-setup",
    call: () => fetchProcessingManagerSetup("movie", "/in", "/out"),
    says: "Could not check your media managers.",
  },
  {
    route: `GET ${LIBRARIES}/1/library-settings`,
    call: () => fetchLibrarySettings(1),
    says: "Could not load this library's setup.",
  },
  {
    route: `PUT ${LIBRARIES}/1/library-settings`,
    call: () => saveLibrarySettings(1, { library_folders: [] }),
    says: "Could not save this library's setup.",
  },
  {
    route: `POST ${LIBRARIES}/1/library-scan`,
    call: () => triggerLibraryScan(1),
    says: "Weir couldn't start a check. Try again in a moment.",
  },
  {
    route: `GET ${LIBRARIES}/1/library-files`,
    call: () => fetchLibraryFiles(1),
    says: "Could not load this workflow's files.",
  },
  {
    route: `GET ${LIBRARIES}/1/library-overview`,
    call: () => fetchLibraryOverview(1),
    says: "Could not load this workflow's overview.",
  },
  {
    route: `POST ${LIBRARIES}/1/library-files/leave-alone`,
    call: () => setLibraryFileLeaveAlone(1, "/a.mkv", true),
    says: "Weir couldn't save that. The file is as it was; try again.",
  },
  {
    route: `POST ${LIBRARIES}/1/library-schedule`,
    call: () => setLibrarySchedule(1, true, false),
    says: "Could not change the workflow schedule.",
  },
  {
    route: `GET ${LIBRARIES}/1/library-redownloads`,
    call: () => fetchLibraryRedownloads(1),
    says: "Could not load the files missing tracks.",
  },
  {
    route: `POST ${LIBRARIES}/1/library-redownloads`,
    call: () => requestLibraryRedownload(1, "/a.mkv"),
    says: "Weir couldn't ask your media manager. Nothing was deleted; try again.",
  },
  {
    route: "GET /api/v1/processing/library-suggestions",
    call: () => fetchLibrarySuggestions(),
    says: "Could not ask your connections for their folders.",
  },
  {
    route: "POST /api/v1/processing/library-check",
    call: () => checkProposedLibraries({}),
    says: "Could not check those folders.",
  },
  {
    route: `GET ${RULE_SETS}`,
    call: () => fetchProcessingRuleSets(),
    says: "Could not load rule sets.",
  },
  {
    route: `POST ${RULE_SETS}`,
    call: () => createProcessingRuleSet(NO_RULES),
    says: "Could not add that rule set.",
  },
  {
    route: `PUT ${RULE_SETS}/3`,
    call: () => updateProcessingRuleSet(3, NO_RULES),
    says: "Could not save that rule set.",
  },
  {
    route: `DELETE ${RULE_SETS}/3`,
    call: () => deleteProcessingRuleSet(3),
    says: "Could not remove that rule set.",
  },
];

describe("a request the server fails without a reason of its own", () => {
  afterEach(() => vi.unstubAllGlobals());

  it.each(failures)("$route says: $says", async ({ route, call, says }) => {
    stubWire({ [route]: unexpectedProblem() });

    const failure = await call().catch((error: unknown) => error);

    expect(failure).toBeInstanceOf(ApiHttpError);
    expect((failure as ApiHttpError).status).toBe(500);
    expect((failure as ApiHttpError).message).toBe(says);
  });
});
