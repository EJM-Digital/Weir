import { renderHook } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { ProcessingFile } from "../../lib/processing/files-api";
import type { ProcessingJobInspectionRow } from "../../lib/processing/jobs-inspection/types";
import { aCleanJob, aFile, aWriting } from "./pipeline/pipeline-fixtures";
import { useLanes } from "./use-processing-lanes";

const state: { files: ProcessingFile[]; jobs: ProcessingJobInspectionRow[] } = {
  files: [],
  jobs: [],
};

vi.mock("../../lib/activity/use-activity-stream-invalidation", () => ({
  useLiveProgress: () => ({}),
}));
vi.mock("../../lib/processing/files-queries", () => ({
  useProcessingFilesQuery: () => ({
    data: { files: state.files, status_counts: {} },
  }),
}));
vi.mock("../../lib/processing/jobs-inspection/queries", () => ({
  useProcessingJobsInspectionQuery: () => ({ data: { jobs: state.jobs } }),
}));
vi.mock("../../lib/processing/libraries-queries", () => ({
  useProcessingLibrariesQuery: () => ({
    data: [
      {
        id: 1,
        name: "Movies",
        ready_after_seconds: 60,
        next_look_at: "2026-10-02T12:00:00Z",
        scan_interval_seconds: 300,
      },
      { id: 2, name: "TV", ready_after_seconds: 60 },
    ],
  }),
}));

describe("useLanes", () => {
  state.files = [
    aWriting(1, { library_id: 2 }),
    aWriting(2, { library_id: 1 }),
    aFile(3, "unprocessed", { library_id: 1 }),
    aFile(4, "blocked_upstream", { library_id: 1 }),
  ];
  state.jobs = [aCleanJob(9, "leased")];

  it("holds every workflow's files when none is chosen", () => {
    const { result } = renderHook(() => useLanes(null));
    expect(result.current.scoped.working).toHaveLength(3);
    expect(result.current.scoped).toBe(result.current.lanes);
  });

  it("scopes a download's lanes and a library clean's to the chosen workflow", () => {
    const { result } = renderHook(() => useLanes(1));
    expect(result.current.scoped.working.map((item) => item.key)).toEqual([
      "file-2",
      "job-9",
    ]);
    expect(result.current.scoped.waiting).toHaveLength(1);
    expect(result.current.scoped.arriving[0].nextLook).toEqual({
      at: Date.parse("2026-10-02T12:00:00Z"),
      interval: 300,
    });
  });

  it("leaves a workflow's lanes whole so a card that ended is tracked against every workflow", () => {
    const { result } = renderHook(() => useLanes(2));
    expect(result.current.scoped.working.map((item) => item.key)).toEqual([
      "file-1",
    ]);
    expect(result.current.lanes.working).toHaveLength(3);
  });
});
