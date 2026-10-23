import { describe, expect, it } from "vitest";

import {
  DEFAULT_SUBTITLE_SORTERS,
  SORTER_FIELDS,
  SORTER_LABELS,
  csvValues,
  defaultAudioSortersFor,
  dumpSorters,
  parseSorters,
} from "./rule-set-model";

describe("parseSorters", () => {
  it("reads a saved order and drops criteria the server does not know", () => {
    const raw = JSON.stringify([
      { field: "codec", value: "dts", reversed: true },
      { field: "mystery" },
    ]);

    expect(parseSorters(raw, DEFAULT_SUBTITLE_SORTERS)).toEqual([
      { field: "codec", value: "dts", reversed: true },
    ]);
  });

  it("falls back to a fresh copy of the default when the saved order is unusable", () => {
    const result = parseSorters("not json", DEFAULT_SUBTITLE_SORTERS);

    expect(result).toEqual(DEFAULT_SUBTITLE_SORTERS);
    expect(result[0]).not.toBe(DEFAULT_SUBTITLE_SORTERS[0]);
  });
});

it("stores a blank match value as null", () => {
  expect(
    dumpSorters([{ field: "language", value: " ", reversed: false }]),
  ).toBe('[{"field":"language","value":null,"reversed":false}]');
});

it("never ranks by the default flag when quality wins across languages", () => {
  expect(
    defaultAudioSortersFor("quality_all_languages").map((row) => row.field),
  ).not.toContain("default");
});

it("reads a comma list as lower-case codes", () => {
  expect(csvValues("eng, JPN ,,")).toEqual(["eng", "jpn"]);
});

describe("SORTER_LABELS", () => {
  it("has words for every criterion the server ranks by", () => {
    for (const field of SORTER_FIELDS) {
      expect(SORTER_LABELS[field]).toBeTruthy();
    }
  });
});
