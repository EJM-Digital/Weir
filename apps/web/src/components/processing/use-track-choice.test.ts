import { act, renderHook } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type {
  ProcessingFileTrack,
  ProcessingFileTracks,
} from "../../lib/processing/files-api";
import { useTrackChoice } from "./use-track-choice";

function track(over: Partial<ProcessingFileTrack>): ProcessingFileTrack {
  return {
    index: 0,
    type: "audio",
    codec: "aac",
    language: "eng",
    title: null,
    channels: 2,
    default: false,
    forced: false,
    rule_would_keep: true,
    rule_reason: "Kept.",
    ...over,
  };
}

const VIDEO = track({ index: 0, type: "video", codec: "h264", channels: null });
const ENGLISH = track({ index: 1, default: true });
const JAPANESE = track({ index: 2, language: "jpn", rule_would_keep: false });
const SUBTITLE = track({
  index: 3,
  type: "subtitle",
  codec: "subrip",
  channels: null,
  rule_would_keep: false,
});
const ATTACHMENT = track({ index: 4, type: "attachment" as never });

function chosenFrom(streams: ProcessingFileTrack[]) {
  const tracks = {
    file_id: 1,
    relative_path: "Film/film.mkv",
    media_scope: "movie",
    source_fingerprint: {
      device: 0,
      inode: 0,
      size_bytes: 100,
      modified_time_ns: 1,
    },
    streams,
  } as ProcessingFileTracks;
  return renderHook(() => useTrackChoice(tracks));
}

describe("choosing which tracks to keep", () => {
  it("starts from what the saved rules would keep, in the file's order, and sets other kinds of stream aside", () => {
    const { result } = chosenFrom([
      VIDEO,
      ENGLISH,
      JAPANESE,
      SUBTITLE,
      ATTACHMENT,
    ]);

    expect(result.current.keptTracks.map((t) => t.index)).toEqual([0, 1]);
    expect(result.current.dropped.map((t) => t.index)).toEqual([2, 3]);
    expect(result.current.other.map((t) => t.index)).toEqual([4]);
    expect(result.current.validationError).toBeNull();
  });

  it("keeps a track once however many times it is asked to be kept", () => {
    const { result } = chosenFrom([VIDEO, ENGLISH, JAPANESE]);

    act(() => result.current.setKeep(JAPANESE, true));
    act(() => result.current.setKeep(JAPANESE, true));

    expect(result.current.choice().order).toEqual([0, 1, 2]);
  });

  it("moves a track one place, and not past either end", () => {
    const { result } = chosenFrom([VIDEO, ENGLISH, JAPANESE]);
    act(() => result.current.setKeep(JAPANESE, true));

    act(() => result.current.move(0, -1));
    expect(result.current.choice().order).toEqual([0, 1, 2]);
    act(() => result.current.move(2, 1));
    expect(result.current.choice().order).toEqual([0, 1, 2]);
    act(() => result.current.move(2, -1));
    expect(result.current.choice().order).toEqual([0, 2, 1]);
    act(() => result.current.move(3, 1));
    expect(result.current.choice().order).toEqual([0, 2, 1]);
  });

  it("makes the first kept audio track the default when none is marked, and a video track never default or forced", () => {
    const { result } = chosenFrom([
      VIDEO,
      { ...ENGLISH, default: false },
      JAPANESE,
    ]);

    expect(result.current.choice().keep).toEqual([
      { index: 0, default: false, forced: false },
      { index: 1, default: true, forced: false },
    ]);
  });

  it("marks a subtitle default only while it is kept and chosen", () => {
    const { result } = chosenFrom([VIDEO, ENGLISH, SUBTITLE]);

    act(() => result.current.setDefault("subtitle", 3));
    expect(result.current.hasDefaultSubtitle).toBe(false);

    act(() => result.current.setKeep(SUBTITLE, true));
    expect(result.current.hasDefaultSubtitle).toBe(true);
    expect(result.current.isDefault(SUBTITLE)).toBe(true);

    act(() => result.current.setDefault("subtitle", null));
    expect(result.current.hasDefaultSubtitle).toBe(false);
  });

  it("says what is missing when no video or audio track is kept", () => {
    const { result } = chosenFrom([VIDEO, ENGLISH]);

    act(() => result.current.setKeep(ENGLISH, false));
    expect(result.current.validationError).toBe(
      "Keep at least one audio track.",
    );
    act(() => result.current.setKeep(VIDEO, false));
    expect(result.current.validationError).toBe(
      "Keep at least one video track.",
    );
  });

  it("names a track by its place among its own kind", () => {
    const { result } = chosenFrom([VIDEO, ENGLISH, JAPANESE]);

    expect(result.current.labelOf(JAPANESE)).toMatch(/^Audio 2/);
  });

  it("offers nothing before the tracks have been read", () => {
    const { result } = renderHook(() => useTrackChoice(undefined));

    expect(result.current.selectable).toEqual([]);
    expect(result.current.choice()).toEqual({ keep: [], order: [] });
  });
});
