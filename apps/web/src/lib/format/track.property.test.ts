import fc from "fast-check";
import { expect, it } from "vitest";

import { assertProperty } from "../../test/property";
import {
  audioCodecName,
  channelLayout,
  describeTrack,
  languageName,
  positionsByKind,
  subtitleTypeName,
  videoCodecName,
} from "./track";

/** Text a file can carry in a codec, language or title field, including the names every object has (`constructor`, `toString`). */
const fileText = fc.oneof(
  fc.string({ unit: "binary" }),
  fc.constantFrom(
    "constructor",
    "toString",
    "valueOf",
    "__proto__",
    "hasOwnProperty",
    "AAC",
    "EAC3",
    "und",
    "",
  ),
);

it("names a codec, a subtitle type and a language in text whatever the file calls them", () => {
  assertProperty(
    fc.property(fileText, (text) => {
      expect(typeof audioCodecName(text)).toBe("string");
      expect(typeof videoCodecName(text)).toBe("string");
      expect(typeof subtitleTypeName(text)).toBe("string");
      expect(typeof languageName(text)).toBe("string");
    }),
  );
});

it("names a codec the same in any case, and an unknown one in capitals", () => {
  assertProperty(
    fc.property(fileText, (text) => {
      expect(audioCodecName(text.toUpperCase())).toBe(
        audioCodecName(text.toLowerCase()),
      );
      expect(videoCodecName(text.toUpperCase())).toBe(
        videoCodecName(text.toLowerCase()),
      );
    }),
  );
});

it("gives a channel count as text, and the layouts people know for the counts they are", () => {
  assertProperty(
    fc.property(fc.integer({ min: 0, max: 64 }), (channels) => {
      const layout = channelLayout(channels);
      expect(typeof layout).toBe("string");
      if (channels === 6) expect(layout).toBe("5.1");
      if (![1, 2, 6, 8].includes(channels)) {
        expect(layout).toBe(`${channels} channels`);
      }
    }),
  );
});

it("describes a track by its kind and its place, whatever the kind is called", () => {
  const kinds: Record<string, string> = {
    video: "Video",
    audio: "Audio",
    subtitle: "Subtitle",
  };
  const track = fc.record({
    type: fc.oneof(
      fc.constantFrom("audio", "video", "subtitle", "constructor", "data"),
      fileText,
    ),
    language: fc.option(fileText, { nil: undefined }),
    codec: fc.option(fileText, { nil: undefined }),
    channels: fc.option(fc.integer({ min: 0, max: 16 }), { nil: undefined }),
    title: fc.option(fileText, { nil: undefined }),
    forced: fc.boolean(),
  });
  assertProperty(
    fc.property(
      track,
      fc.integer({ min: 1, max: 99 }),
      (described, position) => {
        const kind = Object.hasOwn(kinds, described.type)
          ? kinds[described.type]
          : "Track";
        expect(describeTrack(described, position)).toMatch(
          new RegExp(`^${kind} ${position}(?: · |$)`),
        );
      },
    ),
  );
});

it("counts each kind of track from 1 in the order of the file, whatever order they are listed in", () => {
  const tracks = fc
    .uniqueArray(fc.integer({ min: 0, max: 40 }), { maxLength: 12 })
    .chain((indexes) =>
      fc
        .array(fc.constantFrom("video", "audio", "subtitle"), {
          minLength: indexes.length,
          maxLength: indexes.length,
        })
        .map((kinds) =>
          indexes.map((index, at) => ({ index, type: kinds[at] })),
        ),
    );
  assertProperty(
    fc.property(tracks, (listed) => {
      const positions = positionsByKind(listed);
      expect(positions.size).toBe(listed.length);
      for (const kind of ["video", "audio", "subtitle"]) {
        const ofKind = listed
          .filter((track) => track.type === kind)
          .sort((a, b) => a.index - b.index);
        expect(ofKind.map((track) => positions.get(track.index))).toEqual(
          ofKind.map((_, at) => at + 1),
        );
      }
    }),
  );
});
