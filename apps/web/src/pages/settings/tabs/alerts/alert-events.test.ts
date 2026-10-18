import { describe, expect, it } from "vitest";

import { eventLabel, orderedEvents } from "./alert-events";

describe("eventLabel", () => {
  it("says each event the server knows in plain words", () => {
    expect(eventLabel("processing_job_completed")).toBe("A file finished");
    expect(eventLabel("processing_job_failed")).toBe("A file failed for good");
    expect(eventLabel("job_failed")).toBe("Anything failed for good");
    expect(eventLabel("job_completed")).toBe("Anything finished");
  });

  it("shows an event it has no words for as the server names it", () => {
    expect(eventLabel("library_scan_finished")).toBe("library_scan_finished");
  });
});

describe("orderedEvents", () => {
  it("puts the known events in reading order whatever order the server lists them", () => {
    expect(
      orderedEvents([
        "job_completed",
        "job_failed",
        "processing_job_failed",
        "processing_job_completed",
      ]),
    ).toEqual([
      "processing_job_completed",
      "processing_job_failed",
      "job_failed",
      "job_completed",
    ]);
  });

  it("keeps a newer event after the known ones, and offers only what the server supports", () => {
    expect(
      orderedEvents(["library_scan_finished", "job_failed", "job_completed"]),
    ).toEqual(["job_failed", "job_completed", "library_scan_finished"]);
  });

  it("offers every known event before the server has said which it supports", () => {
    expect(orderedEvents(undefined)).toEqual([
      "processing_job_completed",
      "processing_job_failed",
      "job_failed",
      "job_completed",
    ]);
  });
});
