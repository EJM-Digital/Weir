import { afterEach, beforeEach, expect, it, vi } from "vitest";

/** The canvas context is made once per page, so each test loads the module afresh with the canvas it wants. */
async function canvasMeasureOn(context: unknown) {
  vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(
    context as never,
  );
  return (await import("./measure-text")).canvasMeasure;
}

beforeEach(() => vi.resetModules());
afterEach(() => vi.restoreAllMocks());

it("measures text on the canvas in the font it is given", async () => {
  const canvas = {
    font: "",
    measureText: (text: string) => ({ width: text.length * 5 }),
  };
  const canvasMeasure = await canvasMeasureOn(canvas);

  const measure = canvasMeasure("14px sans-serif");

  expect(measure?.("abcd")).toBe(20);
  expect(canvas.font).toBe("14px sans-serif");
});

it("has nothing to measure with when there is no font to measure in", async () => {
  const canvasMeasure = await canvasMeasureOn({
    font: "",
    measureText: () => ({ width: 0 }),
  });

  expect(canvasMeasure("")).toBeNull();
});

it("has nothing to measure with where the browser has no canvas", async () => {
  const canvasMeasure = await canvasMeasureOn(null);

  expect(canvasMeasure("14px sans-serif")).toBeNull();
});
