import { describe, expect, it } from "vitest";

import { connectionRequestCases } from "../../test/wire/connections.cases";
import { homeNetworkRequestCases } from "../../test/wire/home-network.cases";
import { activityRequestCases } from "../../test/wire/files.cases";
import { workflowRequestCases } from "../../test/wire/libraries.cases";
import { ruleRequestCases } from "../../test/wire/rule-sets.cases";

/** Every file under lib/ as text, by its path from lib/. */
const sources = Object.fromEntries(
  Object.entries(
    import.meta.glob("/src/lib/**/*.ts", {
      query: "?raw",
      import: "default",
      eager: true,
    }) as Record<string, string>,
  ).map(([file, text]) => [file.replace("/src/lib/", ""), text]),
);

const REQUEST_CALL =
  /\b(?:apiFetch|sendJson|sendJsonUnchecked|fetchCsrfToken)\(/;

const requestFiles = Object.keys(sources).filter(
  (file) =>
    !/\.test\.|\/generated\/|^api\/client\.ts$/.test(file) &&
    REQUEST_CALL.test(sources[file]!),
);

/**
 * The files whose request functions have not got a fetch-level case yet, and where that is tracked. A file leaves this
 * list when its cases are written; a request file that is in neither list fails below.
 */
const NOT_YET_COVERED: Record<string, string> = {
  "api/auth-api.ts": "#1142",
  "pause/pause-api.ts": "#1142",
  "processing/jobs-inspection/api.ts": "#1142",
  "processing/operator-settings-api.ts": "#1142",
  "processing/overview-stats-api.ts": "#1142",
  "processing/runtime-settings-api.ts": "#1142",
  "system/directory-browser-api.ts": "#1142",
  "system/media-tools.ts": "#1142",
  "system/readiness-api.ts": "#1142",
  "system/system-log-api.ts": "#1142",
  "system/system-stats-api.ts": "#1142",
  "system/system-tasks.ts": "#1142",
};

/** The helper every request goes through; each case exercises it, so it has no cases of its own. */
const SHARED_HELPERS = ["api/send-json.ts"];

/** A file only some of whose request functions are covered: the pattern the covered ones match. */
const COVERED_PART_OF: Record<string, RegExp> = {
  "settings/settings-api.ts": /NotificationChannel/,
};

/** The exported functions of a file that make a request themselves. */
function requestFunctions(text: string): string[] {
  return text
    .split(/^export /m)
    .slice(1)
    .filter((part) => REQUEST_CALL.test(part))
    .map((part) => /^(?:async )?function (\w+)/.exec(part)?.[1])
    .filter((name): name is string => name !== undefined);
}

const covered = new Set(
  [
    ...activityRequestCases,
    ...workflowRequestCases,
    ...ruleRequestCases,
    ...connectionRequestCases,
    ...homeNetworkRequestCases,
  ].map((testCase) => testCase.fn.name),
);

describe("every request builder has a fetch-level case", () => {
  it("finds the request files it is meant to guard", () => {
    expect(requestFiles).toContain("processing/files-api.ts");
    expect(requestFiles).toContain("media-managers/media-managers-api.ts");
  });

  it("lists only files that still make requests", () => {
    for (const file of Object.keys(NOT_YET_COVERED)) {
      expect(requestFiles, `${file} no longer makes requests`).toContain(file);
    }
  });

  it.each(
    requestFiles
      .filter((file) => !(file in NOT_YET_COVERED))
      .filter((file) => !SHARED_HELPERS.includes(file)),
  )("%s: every function that makes a request has a case", (file) => {
    const part = COVERED_PART_OF[file];
    const wanted = requestFunctions(sources[file]!).filter(
      (name) => !part || part.test(name),
    );

    expect(wanted.length, "no request function found to check").toBeGreaterThan(
      0,
    );
    const uncovered = wanted.filter((name) => !covered.has(name));

    expect(uncovered).toEqual([]);
  });
});
