import { describe, expect, it } from "vitest";

import {
  processingFileLead,
  processingFileStatusLabel,
  type ProcessingFile,
  type ProcessingFilesPage,
} from "../processing/files-api";
import {
  withStatusCounts,
  type LibraryOverview,
} from "../processing/library-mode-api";

import { documentedReply, problemsWith } from "../../test/openapi-shape";
import {
  recordedBody,
  recordedRoutes,
  recordedStatus,
} from "../../test/recorded";

/** `METHOD /path?query (note)` as the method and the path, for matching against the server's document. */
function requestOf(route: string): { method: string; path: string } {
  const [method, target] = route.split(" ");
  return { method: method!, path: target!.split("?")[0]! };
}

const succeeded = recordedRoutes().filter((route) => {
  const status = recordedStatus(route);
  return status >= 200 && status < 300 && recordedBody(route) !== null;
});

describe("what the server really answered fits what its document promises", () => {
  it("has recordings to check", () => {
    expect(succeeded.length).toBeGreaterThan(40);
  });

  it.each(succeeded)("%s", (route) => {
    const { method, path } = requestOf(route);
    const schema = documentedReply(method, path, recordedStatus(route));

    expect(
      schema,
      `the document describes no ${recordedStatus(route)} body for ${method} ${path}`,
    ).toBeDefined();
    expect(problemsWith(schema!, recordedBody(route))).toEqual([]);
  });
});

describe("the web's own readers on what the server really sent", () => {
  it("name a processed file and say what happened to it", () => {
    const [file] = recordedBody<ProcessingFilesPage>(
      "GET /api/v1/processing/files",
    ).files as ProcessingFile[];

    expect(processingFileStatusLabel(file!)).toBe("Done");
    expect(processingFileLead(file!)).toBe(`Done. ${file!.status_reason}`);
  });

  it("count a library's files by status as the server counted them", () => {
    const overview = recordedBody<LibraryOverview>(
      "GET /api/v1/processing/libraries/1/library-overview",
    );

    expect(withStatusCounts(overview)).toBe(overview);
    expect(overview.totals.by_status).toEqual({
      needs_cleaning: 1,
      cleaning: 0,
      matches: 0,
      cant_clean_yet: 0,
      left_alone: 0,
    });
  });
});
