import { describe, expect, it } from "vitest";
import { formatRate, formatRatePair, rateScale } from "./rate";

const KB = 1024;
const MB = KB * 1024;

describe("formatRate", () => {
  it("reads under 1 MB/s in whole KB/s", () => {
    expect(formatRate(0)).toBe("0 KB/s");
    expect(formatRate(0.0005 * MB)).toBe("1 KB/s");
    expect(formatRate(120 * KB)).toBe("120 KB/s");
    expect(formatRate(0.5 * MB)).toBe("512 KB/s");
    expect(formatRate(0.999 * MB)).toBe("1023 KB/s");
  });

  it("moves to MB/s at 1 MB/s, including a figure that only rounds up to it", () => {
    expect(formatRate(MB)).toBe("1.0 MB/s");
    expect(formatRate(MB - 1)).toBe("1.0 MB/s");
    expect(formatRate(3.44 * MB)).toBe("3.4 MB/s");
  });

  it("reads one decimal under 10 and whole from 10", () => {
    expect(formatRate(9.94 * MB)).toBe("9.9 MB/s");
    expect(formatRate(9.95 * MB + 1)).toBe("10 MB/s");
    expect(formatRate(10 * MB)).toBe("10 MB/s");
    expect(formatRate(41.6 * MB)).toBe("42 MB/s");
    expect(formatRate(999 * MB)).toBe("999 MB/s");
  });

  it("says a big rate in GB/s from 1000 MB/s, with a tenth under 10", () => {
    expect(formatRate(999.6 * MB)).toBe("1.0 GB/s");
    expect(formatRate(1000 * MB)).toBe("1.0 GB/s");
    expect(formatRate(1127 * MB)).toBe("1.1 GB/s");
    expect(formatRate(12_800 * MB)).toBe("13 GB/s");
  });

  it("reads nothing, a negative or a non-number as zero", () => {
    expect(formatRate(-5)).toBe("0 KB/s");
    expect(formatRate(Number.NaN)).toBe("0 KB/s");
  });
});

describe("rateScale", () => {
  it("picks the unit from the largest value and counts every figure in it", () => {
    const small = rateScale(300 * KB);
    expect(small.unit).toBe(" KB/s");
    expect(small.figure(120 * KB)).toBe("120");
    const mid = rateScale(12 * MB);
    expect(mid.unit).toBe(" MB/s");
    expect(mid.figure(512 * KB)).toBe("0.5");
    expect(mid.figure(12 * MB)).toBe("12");
    const big = rateScale(1127 * MB);
    expect(big.unit).toBe(" GB/s");
    expect(big.figure(1127 * MB)).toBe("1.1");
  });

  it("falls back to KB/s when there is no value", () => {
    expect(rateScale(null).unit).toBe(" KB/s");
  });
});

describe("formatRatePair", () => {
  it("shares the unit when both are in it", () => {
    expect(formatRatePair(4 * MB, 12 * MB)).toEqual({
      first: "4.0",
      second: "12 MB/s",
    });
  });

  it("gives each its own unit when they differ", () => {
    expect(formatRatePair(120 * KB, 12 * MB)).toEqual({
      first: "120 KB/s",
      second: "12 MB/s",
    });
  });
});
