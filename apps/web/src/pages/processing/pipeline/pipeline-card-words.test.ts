import { describe, expect, it } from "vitest";

import type { PipelineCard } from "./pipeline-card-types";
import { buildPipelineCards } from "./pipeline-cards";
import { arrivingWorkflow, deliveringWords } from "./card-words";
import { NOW, aCleanJob, aFile, aWriting, lanesOf } from "./pipeline-fixtures";

const cardOf = (file: Parameters<typeof lanesOf>[0][number]): PipelineCard =>
  buildPipelineCards(lanesOf([file]), [], "all", NOW)[0];

const lines = (card: PipelineCard) =>
  card.details.map((line) =>
    [
      ...line.parts.map((part) =>
        typeof part === "string" ? part : part.bold,
      ),
      line.right ? ` ${line.right}` : "",
    ].join(""),
  );

describe("the words on an arriving file's card", () => {
  it("names the workflow that has it, or the plain word when none is known", () => {
    const arriving = (name: string) =>
      lanesOf([aFile(1, "on_hold", { library_name: name })]).arriving[0];

    expect(arrivingWorkflow(arriving("TV"))).toBe("TV");
    expect(arrivingWorkflow(arriving(""))).toBe("Workflow");
  });
});

describe("the words on a card while a file is checked", () => {
  it("says what the pass is doing, and for how long it has run", () => {
    const card = cardOf(
      aFile(1, "processing", {
        progress_stage: "checking",
        progress_elapsed_seconds: 134,
      }),
    );

    expect(card.status.text).toBe("Checking file");
    expect(lines(card)[0]).toContain("running 2 min 14 s");
  });
});

describe("the words on a card while a file is written", () => {
  it("says a copy is being written when the pass has no percent yet", () => {
    const card = cardOf(
      aFile(1, "processing", {
        progress_stage: "writing",
        progress_percent: null,
        progress_elapsed_seconds: 90,
      }),
    );

    expect(card.status.text).toBe("Writing copy");
    expect(lines(card)[0]).toContain("running 1 min 30 s");
  });

  it("has no remark about time when the pass has not been running", () => {
    const card = cardOf(
      aFile(1, "processing", {
        progress_stage: "writing",
        progress_percent: null,
      }),
    );

    expect(card.details[0].right).toBeUndefined();
  });

  it("drops the speed line when the pass reports none, and the time left when it cannot tell", () => {
    const card = cardOf(
      aWriting(1, {
        progress_speed: null,
        progress_eta_seconds: null,
        progress_elapsed_seconds: null,
      }),
    );

    expect(card.status.text).toBe("42% writing");
    expect(lines(card).some((line) => line.startsWith("Speed"))).toBe(false);
  });
});

describe("the words on a card for a library clean waiting its turn", () => {
  it("carries only its place in line, with no facts to list", () => {
    const [card] = buildPipelineCards(
      lanesOf([], [aCleanJob(8, "pending")]),
      [],
      "all",
      NOW,
    );

    expect(card.details[0]).toMatchObject({ parts: [], right: "#1 in line" });
  });
});

describe("the words on a card while a file is handed over", () => {
  const handing = (source: "library" | "download") =>
    ({
      key: "x",
      source,
      name: "Film",
      path: "Films/Film.mkv",
      libraryName: "Movies",
      facts: "",
    }) as Parameters<typeof deliveringWords>[0];

  it.each([
    ["library", "Replacing file"],
    ["download", "Handing back"],
  ] as const)("says a %s file is being %s", (source, words) => {
    expect(deliveringWords(handing(source)).status.text).toBe(words);
  });
});
