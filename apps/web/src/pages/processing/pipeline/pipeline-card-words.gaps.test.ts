import { describe, expect, it } from "vitest";

import type { PipelineCard } from "./pipeline-card-types";
import { buildPipelineCards } from "./pipeline-cards";
import { NOW, aWriting, lanesOf } from "./pipeline-fixtures";

const cardOf = (file: Parameters<typeof lanesOf>[0][number]): PipelineCard =>
  buildPipelineCards(lanesOf([file]), [], "all", NOW)[0];

const lines = (card: PipelineCard) =>
  card.details.map((line) =>
    line.parts
      .map((part) => (typeof part === "string" ? part : part.bold))
      .join(""),
  );

describe("the lines on a card while a file is written", () => {
  it("has no line for how far through the file the pass is when its length is not known", () => {
    const known = cardOf(aWriting(1, { duration_seconds: 7200 }));
    const unknown = cardOf(aWriting(1, { duration_seconds: null }));

    expect(lines(known).some((line) => / of /.test(line))).toBe(true);
    expect(lines(unknown).some((line) => / of /.test(line))).toBe(false);
  });

  it("has no line for the file's own name when it has none", () => {
    const named = cardOf(aWriting(1));
    const unnamed = cardOf(aWriting(1, { relative_path: "" }));

    expect(named.details.some((line) => line.mono)).toBe(true);
    expect(unnamed.details.some((line) => line.mono)).toBe(false);
  });
});
