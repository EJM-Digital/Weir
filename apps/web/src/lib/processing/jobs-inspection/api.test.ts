import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiHttpError } from "../../api/client";
import { unexpectedProblem } from "../../../test/stub-weir-api";
import { expectChange, expectRead, stubWire } from "../../../test/wire";
import {
  fetchProcessingJobsInspection,
  postProcessingJobCancelPending,
  postProcessingJobRecoverFinalizeFailed,
  processingJobsInspectionPath,
} from "./api";

afterEach(() => vi.unstubAllGlobals());

const INSPECTION = "/api/v1/processing/jobs/inspection";

describe("processingJobsInspectionPath", () => {
  it("builds default recent listing URL", () => {
    expect(processingJobsInspectionPath()).toBe(`${INSPECTION}?limit=100`);
  });

  it("appends repeated status params", () => {
    const p = processingJobsInspectionPath({
      limit: 10,
      statuses: ["pending", "leased"],
    });
    expect(p).toContain("/api/v1/processing/jobs/inspection?");
    expect(p).toContain("limit=10");
    expect(p).toContain("status=pending");
    expect(p).toContain("status=leased");
  });

  it("asks only for jobs whose file Weir still knows when told to", () => {
    expect(processingJobsInspectionPath({ knownFilesOnly: true })).toBe(
      `${INSPECTION}?limit=100&known_files_only=true`,
    );
  });

  it("asks for every job when not told to leave any out", () => {
    expect(processingJobsInspectionPath({ knownFilesOnly: false })).toBe(
      `${INSPECTION}?limit=100`,
    );
    expect(processingJobsInspectionPath({ statuses: [] })).toBe(
      `${INSPECTION}?limit=100`,
    );
  });
});

describe("the job requests", () => {
  const listing = { jobs: [], total: 0 };

  it("reads the listing the path names and hands back what the server sent", async () => {
    const api = stubWire({ [`GET ${INSPECTION}`]: listing });

    await expect(
      fetchProcessingJobsInspection({
        limit: 5,
        statuses: ["pending"],
        knownFilesOnly: true,
      }),
    ).resolves.toEqual(listing);

    expectRead(api, INSPECTION, [
      ["limit", "5"],
      ["status", "pending"],
      ["known_files_only", "true"],
    ]);
  });

  it("cancels a pending job with a POST to its own address", async () => {
    const answer = { job_id: 7, cancelled: true };
    const api = stubWire({
      "POST /api/v1/processing/jobs/7/cancel-pending": answer,
    });

    await expect(postProcessingJobCancelPending(7)).resolves.toEqual(answer);

    expectChange(api, "POST", "/api/v1/processing/jobs/7/cancel-pending", {});
  });

  it("recovers a result that failed to finish with a POST to its own address", async () => {
    const answer = { job_id: 7, recovered: true };
    const api = stubWire({
      "POST /api/v1/processing/jobs/7/recover-finalize-failed": answer,
    });

    await expect(postProcessingJobRecoverFinalizeFailed(7)).resolves.toEqual(
      answer,
    );

    expectChange(
      api,
      "POST",
      "/api/v1/processing/jobs/7/recover-finalize-failed",
      {},
    );
  });

  it.each([
    [
      "listing the jobs",
      `GET ${INSPECTION}`,
      () => fetchProcessingJobsInspection(),
      "Could not load jobs.",
    ],
    [
      "cancelling a job",
      "POST /api/v1/processing/jobs/7/cancel-pending",
      () => postProcessingJobCancelPending(7),
      "Could not cancel job.",
    ],
    [
      "recovering a result",
      "POST /api/v1/processing/jobs/7/recover-finalize-failed",
      () => postProcessingJobRecoverFinalizeFailed(7),
      "Could not recover that result.",
    ],
  ])(
    "say what failed when %s fails without a reason",
    async (_what, route, call, says) => {
      stubWire({ [route]: unexpectedProblem() });

      const failure = await call().catch((error: unknown) => error);

      expect(failure).toBeInstanceOf(ApiHttpError);
      expect((failure as ApiHttpError).message).toBe(says);
    },
  );
});
