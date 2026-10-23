import { afterEach, describe, expect, it, vi } from "vitest";

import {
  CARD_ATTRIBUTE,
  DETAILS_ATTRIBUTE,
  KEEP_ATTRIBUTE,
  TIGHT_ATTRIBUTE,
  fitDetails,
} from "./fit-details";

afterEach(() => vi.restoreAllMocks());

/** A card whose box ends at `cardBottom`, with a block of four detail lines that starts at `blockTop`. */
function cardWithFourLines(cardBottom: number, blockTop: number) {
  const host = document.createElement("div");
  host.innerHTML = `<div ${CARD_ATTRIBUTE}><span ${DETAILS_ATTRIBUTE}>${"<span>line</span>".repeat(4)}</span></div>`;
  const card = host.querySelector<HTMLElement>(`[${CARD_ATTRIBUTE}]`);
  const block = host.querySelector<HTMLElement>(`[${DETAILS_ATTRIBUTE}]`);
  if (!card || !block) throw new Error("the card was not built");
  card.getBoundingClientRect = () => ({ bottom: cardBottom }) as DOMRect;
  block.getBoundingClientRect = () => ({ top: blockTop }) as DOMRect;
  return { block, lines: Array.from(block.children) as HTMLElement[] };
}

describe("fitting a card's detail lines to its height", () => {
  it("shows as many whole lines as the room under the bar holds", () => {
    const { block, lines } = cardWithFourLines(500, 440);

    fitDetails(block.parentElement?.parentElement as HTMLElement);

    expect(lines.map((line) => line.style.display)).toEqual([
      "",
      "",
      "",
      "none",
    ]);
    expect(block.style.display).toBe("");
  });

  it("takes the whole block away, margin and all, when not even one line fits", () => {
    const { block, lines } = cardWithFourLines(419, 400);

    fitDetails(block.parentElement?.parentElement as HTMLElement);

    expect(block.style.display).toBe("none");
    expect(lines.every((line) => line.style.display === "none")).toBe(true);
  });

  it("measures again from scratch each time, so a card that grows gets its lines back", () => {
    const { block, lines } = cardWithFourLines(419, 400);
    const host = block.parentElement?.parentElement as HTMLElement;
    fitDetails(host);

    (host.firstElementChild as HTMLElement).getBoundingClientRect = () =>
      ({ bottom: 520 }) as DOMRect;
    fitDetails(host);

    expect(lines.map((line) => line.style.display)).toEqual(["", "", "", ""]);
    expect(block.style.display).toBe("");
  });
});

/**
 * A card that keeps its first line, whose detail block starts `twoLineTop` under a two-line title and
 * `oneLineTop` under a one-line one, in a card that ends at 500.
 */
function cardKeepingOneLine(twoLineTop: number, oneLineTop: number) {
  const host = document.createElement("div");
  host.innerHTML = `<div ${CARD_ATTRIBUTE}><span ${DETAILS_ATTRIBUTE} ${KEEP_ATTRIBUTE}="1"><span>reason</span><span>name</span></span></div>`;
  const card = host.querySelector<HTMLElement>(`[${CARD_ATTRIBUTE}]`);
  const block = host.querySelector<HTMLElement>(`[${DETAILS_ATTRIBUTE}]`);
  if (!card || !block) throw new Error("the card was not built");
  card.getBoundingClientRect = () => ({ bottom: 500 }) as DOMRect;
  block.getBoundingClientRect = () =>
    ({
      top: card.hasAttribute(TIGHT_ATTRIBUTE) ? oneLineTop : twoLineTop,
    }) as DOMRect;
  return { host, card, block };
}

describe("a card that must keep its first detail line", () => {
  it("gives its title's second line up for the line when only that makes room", () => {
    const { host, card, block } = cardKeepingOneLine(485, 476);

    fitDetails(host);

    expect(card).toHaveAttribute(TIGHT_ATTRIBUTE);
    expect((block.children[0] as HTMLElement).style.display).toBe("");
    expect((block.children[1] as HTMLElement).style.display).toBe("none");
  });

  it("keeps its two-line title when the line fits without giving it up", () => {
    const { host, card, block } = cardKeepingOneLine(460, 440);

    fitDetails(host);

    expect(card).not.toHaveAttribute(TIGHT_ATTRIBUTE);
    expect((block.children[0] as HTMLElement).style.display).toBe("");
  });

  it("keeps its two-line title, and shows no line, when even one line of title leaves no room", () => {
    const { host, card, block } = cardKeepingOneLine(495, 490);

    fitDetails(host);

    expect(card).not.toHaveAttribute(TIGHT_ATTRIBUTE);
    expect(block.style.display).toBe("none");
  });

  it("takes the one-line title back when the card grows", () => {
    const { host, card } = cardKeepingOneLine(485, 476);
    fitDetails(host);
    card.getBoundingClientRect = () => ({ bottom: 560 }) as DOMRect;

    fitDetails(host);

    expect(card).not.toHaveAttribute(TIGHT_ATTRIBUTE);
  });
});

describe("a card with no detail block", () => {
  it("is passed over, and the cards beside it are still fitted", () => {
    const { block } = cardWithFourLines(500, 440);
    const host = block.parentElement?.parentElement as HTMLElement;
    const bare = document.createElement("div");
    bare.setAttribute(CARD_ATTRIBUTE, "");
    host.insertBefore(bare, host.firstChild);

    fitDetails(host);

    expect(bare.children).toHaveLength(0);
    expect(block.style.display).toBe("");
  });
});
