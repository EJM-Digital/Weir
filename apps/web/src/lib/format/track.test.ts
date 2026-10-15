import { describe, expect, it } from "vitest";

import {
  audioCodecName,
  channelLayout,
  describeTrack,
  languageName,
  positionsByKind,
  subtitleTypeName,
  videoCodecName,
} from "./track";

describe("describeTrack", () => {
  it("names an audio track by its place, language, layout and codec", () => {
    expect(
      describeTrack(
        { type: "audio", language: "fre", codec: "eac3", channels: 6 },
        2,
      ),
    ).toBe("Audio 2 · French · 5.1 E-AC-3");
  });

  it("says whether a subtitle is text or a picture, in words", () => {
    expect(
      describeTrack(
        { type: "subtitle", language: "eng", codec: "hdmv_pgs_subtitle" },
        1,
      ),
    ).toBe("Subtitle 1 · English · PGS image");
    expect(
      describeTrack({ type: "subtitle", language: "ger", codec: "subrip" }, 3),
    ).toBe("Subtitle 3 · German · SRT text");
  });

  it("adds forced and the track's own title at the end", () => {
    expect(
      describeTrack(
        {
          type: "subtitle",
          language: "eng",
          codec: "ass",
          forced: true,
          title: "Signs",
        },
        1,
      ),
    ).toBe("Subtitle 1 · English · ASS text · Forced · Signs");
  });

  it("says when a file does not name a track's language", () => {
    expect(
      describeTrack({ type: "audio", language: "und", codec: "aac" }, 1),
    ).toBe("Audio 1 · Unknown language · AAC");
  });

  it("names a video track by its codec alone", () => {
    expect(describeTrack({ type: "video", codec: "h264" }, 1)).toBe(
      "Video 1 · H.264",
    );
  });
});

describe("track parts", () => {
  it("keeps a language the file already spells out", () => {
    expect(languageName("English")).toBe("English");
    expect(languageName("")).toBe("Unknown language");
  });

  it("reads uncommon channel counts as a number of channels", () => {
    expect(channelLayout(8)).toBe("7.1");
    expect(channelLayout(3)).toBe("3 channels");
  });

  it("spells out a video codec it does not know in capitals", () => {
    expect(videoCodecName("hevc")).toBe("HEVC");
    expect(videoCodecName("prores")).toBe("PRORES");
  });

  it("counts each kind of track on its own, in file order", () => {
    const positions = positionsByKind([
      { index: 3, type: "subtitle" },
      { index: 0, type: "video" },
      { index: 2, type: "audio" },
      { index: 1, type: "audio" },
    ]);
    expect(positions.get(1)).toBe(1);
    expect(positions.get(2)).toBe(2);
    expect(positions.get(3)).toBe(1);
  });
});

it("treats a name every object has as a codec or kind nobody listed", () => {
  expect(audioCodecName("constructor")).toBe("CONSTRUCTOR");
  expect(videoCodecName("toString")).toBe("TOSTRING");
  expect(subtitleTypeName("__proto__")).toBe("__PROTO__");
  expect(languageName("constructor")).toBe("constructor");
  expect(describeTrack({ type: "constructor" }, 1)).toBe(
    "Track 1 · Unknown language",
  );
});
