/** The multiples of a power of ten a chart's top may be: 1, 2, 2.5, 5, 10 times 10^n. */
const ROUND_STEPS = [1, 2, 2.5, 5, 10];

/** The top of a chart's scale when nothing has been measured, so an empty chart still has a scale. */
const EMPTY_SCALE_TOP = 1;

/** The tops of a chart that counts whole things, below the first power of ten whose steps are all multiples of 4. */
const SMALL_WHOLE_TOPS = [4, 8];

/** The steps of a counting chart's top above those: 1, 2, 4, 10 times 10^n, keeping the multiples of 4. */
const WHOLE_STEPS = [1, 2, 4, 10];

/** Every quarter of a counting chart's scale is a whole number, so its top is a multiple of this. */
const QUARTERS = 4;

export type RoundScaleOptions = {
  /**
   * For a chart that counts whole things and draws a gridline at every quarter of its top: the top is 4, 8,
   * 20, 40, 100, 200, 400 and so on, so no quarter reads as a fraction of a file.
   */
  wholeNumbers?: boolean;
};

function wholeScaleTop(highest: number): number {
  const shown = Math.max(highest, SMALL_WHOLE_TOPS[0]);
  for (let decade = 1; ; decade *= 10) {
    const tops =
      decade === 1
        ? SMALL_WHOLE_TOPS
        : WHOLE_STEPS.map((step) => step * decade).filter(
            (top) => top % QUARTERS === 0,
          );
    const top = tops.find((candidate) => candidate >= shown);
    if (top !== undefined) return top;
  }
}

/**
 * The top of a chart's scale: the next of 1, 2, 2.5, 5 or 10 times a power of ten at or above the highest
 * value shown, so the gridlines read as round numbers. With `wholeNumbers`, the next top whose quarters
 * are whole numbers.
 */
export function roundScaleTop(
  highest: number,
  { wholeNumbers = false }: RoundScaleOptions = {},
): number {
  const measured = highest > 0 && Number.isFinite(highest) ? highest : 0;
  if (wholeNumbers) return wholeScaleTop(measured);
  if (measured === 0) return EMPTY_SCALE_TOP;
  const decade = 10 ** Math.floor(Math.log10(measured));
  return ROUND_STEPS.map((step) => step * decade).find(
    (top) => top >= measured,
  ) as number;
}
