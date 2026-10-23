import { useLayoutEffect, useRef, useState } from "react";

import type { PauseState } from "../../lib/pause/pause-api";
import {
  useAppClockFormatter,
  useAppDateFormatter,
  parseAppTime,
} from "../../lib/ui/mm-format-date";
import { canvasMeasure, lineWords } from "../../lib/ui/measure-text";
import { Chip } from "../panels/chip";
import { pausePillWords } from "./pause-words";

/**
 * The most of the header's width the pill may take, so a long wording gives way before the page's own controls do.
 * Where the header wraps (a phone, a narrow window) the pill has a row beside Pause, and so may take more.
 */
const HEADER_SHARE = 0.2;
const WRAPPED_HEADER_SHARE = 0.5;

/** The font a pill is set in, as a canvas needs it: the `font` shorthand reads back empty in some browsers. */
function fontOf(element: HTMLElement): string {
  const { fontStyle, fontWeight, fontSize, fontFamily } =
    getComputedStyle(element);
  return `${fontStyle} ${fontWeight} ${fontSize} ${fontFamily}`;
}

/**
 * Which of the wordings fits a share of the header: measured in the pill's own font before paint, and again when
 * the header changes size or the fonts arrive. Without a header to measure, or a canvas, the fullest wording shows.
 */
function useFittingWords(words: readonly string[]) {
  const pillRef = useRef<HTMLSpanElement | null>(null);
  const [chosen, setChosen] = useState(words[0]);
  const wordsKey = words.join("\u0000");
  useLayoutEffect(() => {
    const options = wordsKey.split("\u0000");
    const pill = pillRef.current;
    const header = pill?.closest<HTMLElement>(".mm-header");
    if (!pill || !header) return undefined;
    let live = true;
    const fit = () => {
      if (!live) return;
      const measure = canvasMeasure(fontOf(pill));
      const text = Array.from(pill.childNodes)
        .slice(-1)
        .map((node) => node.textContent)
        .join("");
      if (!measure || header.clientWidth <= 0) {
        setChosen(options[0]);
        return;
      }
      // The dot, the padding and the border are what the pill adds to its words.
      const chrome = pill.getBoundingClientRect().width - measure(text);
      const share =
        getComputedStyle(header).flexWrap === "nowrap"
          ? HEADER_SHARE
          : WRAPPED_HEADER_SHARE;
      const room = header.clientWidth * share - chrome;
      setChosen(lineWords(options, room, measure));
    };
    fit();
    const observer =
      typeof ResizeObserver === "undefined" ? null : new ResizeObserver(fit);
    observer?.observe(header);
    void document.fonts?.ready.then(fit);
    return () => {
      live = false;
      observer?.disconnect();
    };
  }, [wordsKey]);
  return { pillRef, shown: words.includes(chosen) ? chosen : words[0] };
}

/** The pill that says Weir is paused and how it ends, in the fullest wording the header has room for. */
export function PausePill({ pause }: { pause: PauseState }) {
  const formatDate = useAppDateFormatter();
  const formatClock = useAppClockFormatter();
  const endsAt = parseAppTime(pause.paused_until);
  const words = pausePillWords(
    pause.paused_until && endsAt !== null
      ? { full: formatDate(pause.paused_until), clock: formatClock(endsAt) }
      : null,
  );
  const { pillRef, shown } = useFittingWords(words);
  return (
    <Chip
      meaning="attention"
      data-testid="pause-badge"
      title={pause.reason}
      ref={pillRef}
    >
      {shown}
    </Chip>
  );
}
