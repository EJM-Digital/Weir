import { afterEach, describe, expect, it, vi } from "vitest";

import { connectionRequestCases } from "../../test/wire/connections.cases";
import { activityRequestCases } from "../../test/wire/files.cases";
import { workflowRequestCases } from "../../test/wire/libraries.cases";
import { processingSettingsRequestCases } from "../../test/wire/processing-settings.cases";
import {
  describeRequests,
  type RequestCase,
} from "../../test/wire/request-case";
import { ruleRequestCases } from "../../test/wire/rule-sets.cases";
import { stubWire } from "../../test/wire";
import { unexpectedProblem } from "../../test/stub-weir-api";
import { ApiHttpError } from "../api/client";
import * as directPlay from "./direct-play-api";
import * as fileRemuxPass from "./file-remux-pass-api";
import * as files from "./files-api";
import * as filesAtOnce from "./files-at-once-api";
import * as keptFiles from "./kept-files-api";
import * as maintenance from "./maintenance-api";
import * as operatorSettings from "./operator-settings-api";
import * as overviewStats from "./overview-stats-api";
import * as rejectedFiles from "./rejected-files-api";
import * as rulesPreview from "./rules-preview-api";
import * as runtimeSettings from "./runtime-settings-api";
import * as watchedFolderScan from "./watched-folder-scan-api";

/**
 * The mutation run of these files runs only the tests under lib/processing, so the fetch-level cases that hold each request
 * to its method, address and body are run here as well, for the request functions of the files it mutates.
 */
const OWN_FUNCTIONS = new Set<unknown>(
  [
    directPlay,
    fileRemuxPass,
    files,
    filesAtOnce,
    keptFiles,
    maintenance,
    operatorSettings,
    overviewStats,
    rejectedFiles,
    rulesPreview,
    runtimeSettings,
    watchedFolderScan,
  ].flatMap((module) => Object.values(module)),
);

const cases: RequestCase[] = [
  ...activityRequestCases,
  ...workflowRequestCases,
  ...ruleRequestCases,
  ...connectionRequestCases,
  ...processingSettingsRequestCases,
].filter((testCase) => OWN_FUNCTIONS.has(testCase.fn));

describeRequests("The processing file and settings requests", cases);

/** What each request says when the server fails without a reason of its own to give: the words a person reads. */
const FAILURE_WORDS: Record<string, string> = {
  fetchDirectPlayDevices: "Could not load the Direct Play devices.",
  putDirectPlayDevices: "Could not save your Direct Play devices.",
  postProcessingFileRemuxPassEnqueue: "Could not queue that file.",
  fetchProcessingFiles: "Could not load files.",
  fetchProcessingFileRemoveOptions:
    "Could not read what removing this file would do.",
  forgetProcessingFile: "Could not remove that file from the list.",
  moveProcessingFileToTop:
    "Could not move that file to the front of the queue.",
  requeueProcessingFile: "Could not queue that file again.",
  requeueProcessingFiles: "Could not queue those files again.",
  fetchProcessingWhyHeld: "Could not ask why that file is held.",
  fetchProcessingFileLog:
    "Weir couldn't load what happened to this file. Try again.",
  fetchProcessingFileTracks: "Could not read that file's tracks.",
  postProcessingManualPlan: "Could not queue that track choice.",
  fetchProcessingFilesAtOnce: "Could not load what is running.",
  fetchKeptFiles: "Could not load kept files.",
  processKeptFileAgain: "Could not process that file again.",
  fetchProcessingMaintenance: "Could not read the maintenance state.",
  runProcessingMaintenance: "Could not start that maintenance job.",
  putProcessingOperatorSettings: "Could not save processing settings.",
  fetchProcessingOperatorSettings: "Could not load processing settings.",
  fetchProcessingOverviewStats: "Could not load processing stats.",
  fetchRejectedFilesSummary: "Could not count the rejected files.",
  processRejectedFilesAgain: "Could not process the rejected files again.",
  previewProcessingRules: "Could not preview these rules on that file.",
  fetchProcessingRuntimeSettings: "Could not load runtime settings.",
  postProcessingWatchedFolderRemuxScanDispatchEnqueue:
    "Could not queue watched-folder scan.",
};

describe("a failure the server gives no reason for", () => {
  afterEach(() => vi.unstubAllGlobals());

  it.each(cases)("$fn.name: says what could not be done", async (testCase) => {
    stubWire({
      [`${testCase.method} ${testCase.path}`]: unexpectedProblem(),
    });

    const failure = await testCase.call().catch((error: unknown) => error);

    expect(failure).toBeInstanceOf(ApiHttpError);
    expect((failure as ApiHttpError).status).toBe(500);
    expect((failure as ApiHttpError).message).toBe(
      FAILURE_WORDS[testCase.fn.name],
    );
  });

  it("is worded for every request function of these files", () => {
    expect(
      [...new Set(cases.map((testCase) => testCase.fn.name))].sort(),
    ).toEqual(Object.keys(FAILURE_WORDS).sort());
  });
});
