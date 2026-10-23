import { expect, it } from "vitest";

import { describeTrackIn, positionsByKind } from "./track";

const english = { index: 3, type: "audio", language: "eng", codec: "aac" };
const french = { index: 5, type: "audio", language: "fre", codec: "aac" };

it("numbers a track among its own kind by the counts made for the file", () => {
  const positions = positionsByKind([english, french]);

  expect(describeTrackIn(french, positions)).toBe("Audio 2 · French · AAC");
});

it("takes a track the counts do not hold as the first of its kind", () => {
  expect(describeTrackIn(french, new Map())).toBe("Audio 1 · French · AAC");
});
