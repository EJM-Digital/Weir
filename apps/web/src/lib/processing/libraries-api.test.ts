import { describe, expect, it } from "vitest";

import { library } from "../../pages/settings/tabs/libraries/library-test-fixtures";
import {
  PROCESSING_MEDIA_TYPE_LABELS,
  processingMediaTypeBadge,
  writeFromProcessingLibrary,
  type ProcessingLibrary,
} from "./libraries-api";

/** What the server reports about a workflow but never accepts back: it works these out itself. */
const READ_ONLY_FIELDS: readonly (keyof ProcessingLibrary)[] = [
  "id",
  "display_order",
  "effective_max_concurrent_files",
  "manager_coverage",
  "manager_coverage_detail",
  "discovered_from_connection_id",
  "discovered_library_key",
  "folders_synced_from_connection_id",
  "active_job_count",
  "next_look_at",
  "updated_at",
];

describe("writeFromProcessingLibrary", () => {
  it("sends back every setting the server reported, so a save of one part never resets another", () => {
    const stored = library({
      remux_writer: "ffmpeg",
      min_file_size_mb: 12,
      rejected_file_action: "delete_file",
      failure_policy: "hold",
      manager_connection_ids: [4, 9],
    });

    const write: Record<string, unknown> = {
      ...writeFromProcessingLibrary(stored),
    };

    const settings = Object.keys(stored).filter(
      (key) => !READ_ONLY_FIELDS.includes(key as keyof ProcessingLibrary),
    );
    const dropped = settings.filter((key) => !(key in write));
    expect(dropped).toEqual([]);
    for (const key of settings) {
      expect(write[key], key).toEqual(stored[key as keyof ProcessingLibrary]);
    }
  });

  it("carries which tool writes the output", () => {
    const write = writeFromProcessingLibrary(
      library({ remux_writer: "ffmpeg" }),
    );

    expect(write.remux_writer).toBe("ffmpeg");
  });
});

describe("the media type labels", () => {
  it("name the two kinds of library in the words a person reads", () => {
    expect(PROCESSING_MEDIA_TYPE_LABELS).toEqual({
      movie: "Movies",
      tv: "TV episodes",
    });
  });
});

describe("processingMediaTypeBadge", () => {
  const badge = (name: string, media_type: "movie" | "tv") =>
    processingMediaTypeBadge({ name, media_type });

  it("shows the type beside a name that does not say it", () => {
    expect(badge("Films", "movie")).toBe("Movies");
    expect(badge("Anime", "tv")).toBe("TV episodes");
  });

  it("shows nothing for a name that is the type, in any case", () => {
    expect(badge("Movies", "movie")).toBeNull();
    expect(badge("MOVIES", "movie")).toBeNull();
    expect(badge("TV Episodes", "tv")).toBeNull();
  });

  it("shows nothing for a name that is blank once trimmed", () => {
    expect(badge("   ", "movie")).toBeNull();
  });

  it("shows nothing for a name that is a part of the label, or that holds it", () => {
    expect(badge("Movie", "movie")).toBeNull();
    expect(badge("My movies", "movie")).toBeNull();
    expect(badge("  Movies  ", "movie")).toBeNull();
  });

  it.each(["Kids tv", "Kids show", "Kids shows", "Kids series"])(
    "shows nothing for the TV workflow %s, whose name already says TV",
    (name) => {
      expect(badge(name, "tv")).toBeNull();
    },
  );

  it("still shows the type for a TV workflow whose name does not say TV", () => {
    expect(badge("Kids", "tv")).toBe("TV episodes");
    expect(badge("Showcase", "tv")).toBe("TV episodes");
  });

  it("does not read a movie workflow's name as TV", () => {
    expect(badge("Action shows", "movie")).toBe("Movies");
  });
});
