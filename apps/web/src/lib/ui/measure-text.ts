/**
 * What a line of text says in the room it has, decided by measuring the words rather than letting them clip. A line is
 * given its words, the fullest first and the shortest last, and says the fullest that fit and the shortest where none
 * does: a cleaned file says "221 MB saved" where that fits and "221 MB" where it does not. A line none of whose words
 * fit ends in an ellipsis, and the line's title says it whole.
 */

/** The width in px of a line of text in the caption's own font. */
export type MeasureText = (text: string) => number;

/** The room left by the type's own rounding: a line must be this much narrower than the room, or its end is cut. */
const LINE_MARGIN_PX = 1;

/** The canvas context everything is measured on, made once. */
let context: CanvasRenderingContext2D | null | undefined;

function measuringContext(): CanvasRenderingContext2D | null {
  if (context === undefined) {
    context = document.createElement("canvas").getContext("2d");
  }
  return context;
}

/** Measures text in `font` (a CSS font shorthand) on a canvas, or null where the browser has no canvas. */
export function canvasMeasure(font: string): MeasureText | null {
  const canvas = measuringContext();
  if (!canvas || !font) return null;
  return (text) => {
    canvas.font = font;
    return canvas.measureText(text).width;
  };
}

/** The fullest of the words that fit a line `room` px wide, or undefined where none does. */
export function fittingWords(
  words: readonly string[],
  room: number,
  measure: MeasureText,
): string | undefined {
  return words.find((candidate) => measure(candidate) <= room - LINE_MARGIN_PX);
}

/** The words for a line `room` px wide: the fullest that fit, else the shortest. */
export function lineWords(
  words: readonly string[],
  room: number,
  measure: MeasureText,
): string {
  return fittingWords(words, room, measure) ?? words[words.length - 1];
}
