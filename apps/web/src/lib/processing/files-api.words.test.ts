import { describe, expect, it } from "vitest";

import {
  isBelowMinimumSize,
  processingFileLead,
  processingFileLogDownloadPath,
  processingFileStatusLabel,
  wasRemovedBelowMinimumSize,
  type ProcessingFile,
} from "./files-api";

const UNDER_MINIMUM =
  "Skipped because this file is 12.1 MB, under the 50 MB minimum.";

function file(
  status: ProcessingFile["status"],
  reason: string,
  skipKind: string | null = null,
  failureClass: string | null = null,
) {
  return {
    status,
    status_reason: reason,
    failure_class: failureClass,
    skip_kind: skipKind,
  } as ProcessingFile;
}

describe("the plain word for a file's state", () => {
  it.each([
    ["unprocessed", "Waiting"],
    ["processing", "Processing"],
    ["processed", "Done"],
    ["processing_failed", "Failed"],
    ["skipped", "Skipped"],
    ["disabled", "Workflow off"],
    ["on_hold", "On hold"],
    ["out_of_schedule", "Waiting for workflow hours"],
    ["blocked_upstream", "Waiting for your media manager"],
    ["passed_through", "Passed through unchanged"],
    ["rejected", "Rejected for a replacement"],
    ["cancelled", "Cancelled"],
  ] as const)("is the word for a file that is %s", (status, word) => {
    expect(processingFileStatusLabel(file(status, ""))).toBe(word);
  });

  it("is plain Rejected only for a rejection the rules themselves made", () => {
    expect(processingFileStatusLabel(file("rejected", "", null, "rules"))).toBe(
      "Rejected",
    );
    expect(
      processingFileStatusLabel(file("rejected", "", null, "manager")),
    ).toBe("Rejected for a replacement");
    expect(processingFileStatusLabel(file("rejected", "", null, null))).toBe(
      "Rejected for a replacement",
    );
    expect(
      processingFileStatusLabel(file("processing_failed", "", null, "rules")),
    ).toBe("Failed");
  });
});

describe("a skip code on a file that is not skipped", () => {
  const notSkipped = file(
    "processed",
    UNDER_MINIMUM,
    "below_minimum_size_removed",
  );

  it("is not a removal, and says Done", () => {
    expect(wasRemovedBelowMinimumSize(notSkipped)).toBe(false);
    expect(processingFileStatusLabel(notSkipped)).toBe("Done");
    expect(processingFileLead(notSkipped)).toBe(`Done. ${UNDER_MINIMUM}`);
  });

  it("is a below-minimum skip only while the file is skipped", () => {
    expect(
      isBelowMinimumSize(
        file("skipped", UNDER_MINIMUM, "below_minimum_size_removed"),
      ),
    ).toBe(true);
    expect(isBelowMinimumSize(notSkipped)).toBe(false);
  });
});

describe("a file's state and its reason as one lead", () => {
  it("is the word alone when the server gave no reason", () => {
    expect(processingFileLead(file("processed", ""))).toBe("Done");
    expect(processingFileLead(file("on_hold", ""))).toBe("On hold");
  });

  it("puts the word before a reason that does not open with it", () => {
    expect(processingFileLead(file("on_hold", "Until 22:00."))).toBe(
      "On hold. Until 22:00.",
    );
  });

  it("lets a reason that opens with the word, in any case, stand alone", () => {
    expect(processingFileLead(file("on_hold", "on hold until 22:00."))).toBe(
      "on hold until 22:00.",
    );
    expect(
      processingFileLead(file("skipped", "Skipped because it is a sample.")),
    ).toBe("Skipped because it is a sample.");
  });
});

describe("the address of a file's plain-text record", () => {
  it("is the file's log address with /download after it", () => {
    expect(processingFileLogDownloadPath(7)).toBe(
      "/api/v1/processing/files/7/log/download",
    );
  });
});
