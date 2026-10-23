/**
 * A line of words that is never cut: it says the fullest of the words it is given that fits its box, and the whole of
 * them is its tooltip. How wide a line is comes from laying it out, in the box's own type, beside the box, so the
 * letter-spacing, the tabular figures and the case of a card's type are all counted. Which of the words fit is
 * decided by the measured-words helper the poster captions use.
 */
import { useLayoutEffect, useRef, useState, type ReactNode } from "react";

import { lineWords, type MeasureText } from "./measure-text";

/** Words, the fullest first and the shortest last. */
export type Words = readonly string[];

/**
 * Words that narrow as the parts of a line are dropped, the least important part first: `parts` are in the order they
 * read, each with how much it matters. The first set of words is all of them, each next one has one fewer part until
 * only the part that matters most is left, and then come the other parts one at a time, the more important first, for
 * a box too narrow for even the part that matters most.
 */
export function narrowing(
  parts: readonly { text: string; matters: number }[],
): string[] {
  const kept = [...parts];
  const words = [kept.map((part) => part.text).join(" · ")];
  while (kept.length > 1) {
    const least = kept.reduce(
      (lowest, part, index) =>
        part.matters < kept[lowest].matters ? index : lowest,
      0,
    );
    kept.splice(least, 1);
    words.push(kept.map((part) => part.text).join(" · "));
  }
  const others = parts
    .filter((part) => part.text !== kept[0]?.text)
    .sort((a, b) => b.matters - a.matters);
  return [...words, ...others.map((part) => part.text)];
}

/** A name in words that narrow with the room by dropping its last word, whole words only: "Check media managers", "Check media", "Check". */
export function nameWords(name: string): string[] {
  const words = name.split(" ").filter(Boolean);
  return words.map((_, index) =>
    words.slice(0, words.length - index).join(" "),
  );
}

/** Measures text as `box` sets it: in a twin of the box, laid out out of sight beside it, and as wide as the text is. */
function measureAsBox(box: HTMLElement): {
  measure: MeasureText;
  end: () => void;
} {
  const twin = box.cloneNode(false) as HTMLElement;
  twin.removeAttribute("title");
  twin.removeAttribute("data-testid");
  twin.setAttribute("aria-hidden", "true");
  Object.assign(twin.style, {
    position: "absolute",
    visibility: "hidden",
    width: "auto",
    maxWidth: "none",
    overflow: "visible",
    whiteSpace: "nowrap",
  });
  box.after(twin);
  return {
    measure: (text) => {
      twin.textContent = text;
      return twin.getBoundingClientRect().width;
    },
    end: () => twin.remove(),
  };
}

type FitTextProps = {
  words: Words;
  className?: string;
  /** What the line says whole; the fullest words when not given. */
  title?: string;
  /** Hides the line from screen readers, for words that are said to them another way. */
  ariaHidden?: boolean;
  /** Draws the words that are said, where they are not plain text: a bold figure after a lead, say. */
  format?: (words: string) => ReactNode;
};

/**
 * A block of one line that says the fullest of `words` that fits its width, measured before paint and again when the
 * box changes size, the fonts arrive or the words change. Where the box has no width to measure the fullest words
 * show. Its CSS must give it a width of its own, as a grid or flex item or a block does, not one that follows its text.
 */
export function FitText({
  words,
  className,
  title,
  ariaHidden,
  format,
}: FitTextProps) {
  const boxRef = useRef<HTMLSpanElement | null>(null);
  const [chosen, setChosen] = useState(0);
  const wordsKey = words.join("\u0000");
  useLayoutEffect(() => {
    const box = boxRef.current;
    /* istanbul ignore if -- @preserve */
    if (!box) return undefined;
    const options = wordsKey.split("\u0000");
    const fit = () => {
      const room = box.clientWidth;
      if (room <= 0) {
        setChosen(0);
        return;
      }
      const { measure, end } = measureAsBox(box);
      const words = lineWords(options, room, measure);
      end();
      setChosen(Math.max(0, options.indexOf(words)));
    };
    fit();
    const observer =
      typeof ResizeObserver === "undefined" ? null : new ResizeObserver(fit);
    observer?.observe(box);
    void document.fonts?.ready.then(fit);
    return () => observer?.disconnect();
  }, [wordsKey]);
  return (
    <span
      ref={boxRef}
      className={className}
      title={title ?? words[0]}
      aria-hidden={ariaHidden}
    >
      {format
        ? format(words[Math.min(chosen, words.length - 1)])
        : words[Math.min(chosen, words.length - 1)]}
    </span>
  );
}
