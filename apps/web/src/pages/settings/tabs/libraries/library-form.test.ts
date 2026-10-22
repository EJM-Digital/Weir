import { describe, expect, it, vi } from "vitest";

import { EMPTY_LIBRARY_FORM, formFrom, writeFrom } from "./library-form";
import { library } from "./library-test-fixtures";

describe("writeFrom", () => {
  it("sends a blank or unreadable number as the server's default", () => {
    const write = writeFrom({
      ...EMPTY_LIBRARY_FORM,
      name: " Movies ",
      max_attempts: "",
      retry_backoff_seconds: "soon",
    });

    expect(write.name).toBe("Movies");
    expect(write.max_attempts).toBe(3);
    expect(write.retry_backoff_seconds).toBe(300);
  });

  it("sends a blank or unreadable minimum size or wait as the server's default", () => {
    const write = writeFrom({
      ...EMPTY_LIBRARY_FORM,
      min_file_size_mb: "",
      ready_after_seconds: "  ",
    });

    expect(write.min_file_size_mb).toBe(50);
    expect(write.ready_after_seconds).toBe(60);
  });

  it("sends a minimum size or wait of zero as the workflow's own choice", () => {
    const write = writeFrom({
      ...EMPTY_LIBRARY_FORM,
      min_file_size_mb: "0",
      ready_after_seconds: "0",
    });

    expect(write.min_file_size_mb).toBe(0);
    expect(write.ready_after_seconds).toBe(0);
  });

  it("no longer sends the three waits the one wait replaced", () => {
    const write: Record<string, unknown> = { ...writeFrom(EMPTY_LIBRARY_FORM) };

    expect(write).not.toHaveProperty("min_file_age_seconds");
    expect(write).not.toHaveProperty("hold_minutes");
    expect(write).not.toHaveProperty("file_detection_interval_seconds");
  });

  it("sends a blank date window side as no limit", () => {
    const write = writeFrom({ ...EMPTY_LIBRARY_FORM, created_after: "  " });

    expect(write.created_after).toBeNull();
  });

  it("keeps a second manager link the editor does not show", () => {
    const write = writeFrom(
      { ...EMPTY_LIBRARY_FORM, manager_connection_id: "4" },
      {
        manager_connection_ids: [2, 9],
      } as Parameters<typeof writeFrom>[1],
    );

    expect(write.manager_connection_ids).toEqual([4, 9]);
  });

  it("no longer sends settings that have no effect", () => {
    const write = writeFrom(formFrom(library()), library());

    for (const removed of [
      "rewrite_with_ffmpeg",
      "hardware_decode_mode",
      "hardware_device",
      "hardware_disabled_vendors_csv",
    ]) {
      expect(write, removed).not.toHaveProperty(removed);
    }
  });
});

describe("formFrom", () => {
  it("shows a new workflow's wait and minimum size, filled in", () => {
    expect(EMPTY_LIBRARY_FORM.ready_after_seconds).toBe("60");
    expect(EMPTY_LIBRARY_FORM.min_file_size_mb).toBe("50");
  });

  it("shows a workflow's own minimum size and wait, including zero", () => {
    const form = formFrom(
      library({ min_file_size_mb: 200, ready_after_seconds: 0 }),
    );

    expect(form.min_file_size_mb).toBe("200");
    expect(form.ready_after_seconds).toBe("0");
  });

  it("reads a detection window the server wrote without a zone as UTC, wherever the browser is", () => {
    vi.stubEnv("TZ", "Pacific/Auckland");
    try {
      const written = formFrom(
        library({
          created_after: "2026-03-03T18:06:00",
          modified_before: null,
        }),
      );

      expect(written.created_after).toBe("2026-03-04T07:06");
      expect(written.modified_before).toBe("");
    } finally {
      vi.unstubAllEnvs();
    }
  });
});

describe("dates and free space the form cannot read", () => {
  it("shows a stored date it cannot read as blank", () => {
    expect(
      formFrom(library({ created_after: "not a date" })).created_after,
    ).toBe("");
  });

  it("sends a date it cannot read as no limit", () => {
    expect(
      writeFrom({ ...EMPTY_LIBRARY_FORM, created_before: "not a date" })
        .created_before,
    ).toBeNull();
  });

  it("sends a negative amount of free space as the default", () => {
    expect(
      writeFrom({ ...EMPTY_LIBRARY_FORM, minimum_free_disk_space_gb: "-2" })
        .minimum_free_disk_space_mb,
    ).toBe(5 * 1024);
  });
});
