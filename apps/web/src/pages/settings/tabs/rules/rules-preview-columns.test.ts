import { describe, expect, it } from "vitest";

import type { ProcessingRulesPreviewTrack } from "../../../../lib/processing/rules-preview-api";
import { flagWords, PREVIEW_SORT_VALUES } from "./rules-preview-columns";

function track(
  overrides: Partial<ProcessingRulesPreviewTrack>,
): ProcessingRulesPreviewTrack {
  return {
    index: 0,
    type: "audio",
    codec: "eac3",
    language: "eng",
    title: "Surround",
    channels: 6,
    action: "keep",
    default: false,
    forced: false,
    ...overrides,
  } as ProcessingRulesPreviewTrack;
}

describe("flagWords", () => {
  it.each([
    [{}, ""],
    [{ default: true }, "Default"],
    [{ forced: true }, "Forced"],
    [{ default: true, forced: true }, "Default, Forced"],
  ])("names the flags of %j", (flags, words) => {
    expect(flagWords(track(flags))).toBe(words);
  });
});

describe("what the preview's columns sort by", () => {
  it("puts the kinds of track in the order video, audio, subtitle", () => {
    const rank = (type: "video" | "audio" | "subtitle") =>
      PREVIEW_SORT_VALUES.type(track({ type }));

    expect(rank("video")).toBeLessThan(rank("audio"));
    expect(rank("audio")).toBeLessThan(rank("subtitle"));
  });

  it("puts the track kept before the track dropped", () => {
    expect(PREVIEW_SORT_VALUES.action(track({ action: "keep" }))).not.toBe(
      PREVIEW_SORT_VALUES.action(track({ action: "drop" })),
    );
  });

  it.each([
    ["codec", { codec: "hevc" }, "hevc", { codec: "" }],
    ["language", { language: "fra" }, "fra", { language: "" }],
    ["title", { title: "Commentary" }, "Commentary", { title: "" }],
    ["flags", { default: true }, "Default", {}],
  ] as const)(
    "sorts by the %s when it has one, and after the rest when it has none",
    (column, has, value, hasNone) => {
      expect(PREVIEW_SORT_VALUES[column](track(has))).toBe(value);
      expect(PREVIEW_SORT_VALUES[column](track(hasNone))).toBeNull();
    },
  );

  it.each([
    ["an audio track", { type: "audio", channels: 6 }, 6],
    [
      "an audio track with no channel count",
      { type: "audio", channels: 0 },
      null,
    ],
    ["a video track", { type: "video", channels: 6 }, null],
  ] as const)("sorts by channels for %s", (_name, fields, value) => {
    expect(PREVIEW_SORT_VALUES.channels(track(fields))).toBe(value);
  });

  it("sorts by the track's place in the file", () => {
    expect(PREVIEW_SORT_VALUES.index(track({ index: 4 }))).toBe(4);
  });
});
