import { renderHook } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { PipelineCard } from "./pipeline-card-types";
import type { PipelineStage } from "./pipeline-stages";
import { useStageChanges } from "./use-stage-changes";

const card = (key: string, stage: PipelineStage) =>
  ({ key, stage }) as PipelineCard;

function follow(first: PipelineCard[]) {
  return renderHook(({ cards }) => useStageChanges(cards), {
    initialProps: { cards: first },
  });
}

describe("the cards that have moved to another station", () => {
  it("has none when the cards are first seen", () => {
    const { result } = follow([card("a", "queued")]);

    expect(result.current.size).toBe(0);
  });

  it("names a card once it has moved on, and not one that is new to the board", () => {
    const { result, rerender } = follow([card("a", "queued")]);

    rerender({ cards: [card("a", "processing"), card("b", "queued")] });

    expect([...result.current]).toEqual(["a"]);
  });

  it("keeps naming a card that moved while other cards come and go", () => {
    const { result, rerender } = follow([card("a", "queued")]);
    rerender({ cards: [card("a", "processing")] });

    rerender({ cards: [card("a", "processing"), card("b", "queued")] });

    expect([...result.current]).toEqual(["a"]);
  });

  it("forgets a card that has left", () => {
    const { result, rerender } = follow([card("a", "queued")]);
    rerender({ cards: [card("a", "processing")] });

    rerender({ cards: [card("b", "queued")] });

    expect(result.current.size).toBe(0);
  });
});
