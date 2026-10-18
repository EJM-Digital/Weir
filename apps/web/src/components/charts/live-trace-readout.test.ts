import { describe, expect, it } from "vitest";

import { readoutFor, sampleAfterKey } from "./live-trace-readout";

const clock = (time: number) => `t${time / 1000}`;
const percent = (value: number) => `${Math.round(value)}%`;
const window = { from: 0, to: 4000 };

const line = (...values: number[]) =>
  values.map((value, index) => ({ at: index * 1000, value }));

describe("the pointer's readout", () => {
  it("names the time of the nearest sample and its value", () => {
    const readout = readoutFor(
      [{ samples: line(10, 20, 30, 40, 50) }],
      window,
      0.5,
      percent,
      clock,
    );
    expect(readout).toEqual({ text: "t2 · 30%", fraction: 0.5 });
  });

  it("moves to the sample nearest, not the exact place", () => {
    const readout = readoutFor(
      [{ samples: line(10, 20, 30, 40, 50) }],
      window,
      0.4,
      percent,
      clock,
    );
    expect(readout?.text).toBe("t2 · 30%");
  });

  it("names each line when there is more than one", () => {
    const readout = readoutFor(
      [
        { label: "read", samples: line(1, 2, 3, 4, 5) },
        { label: "write", samples: line(10, 20, 30, 40, 50) },
      ],
      window,
      1,
      (value) => `${value} MB/s`,
      clock,
    );
    expect(readout?.text).toBe("t4 · read 5 MB/s · write 50 MB/s");
  });

  it("leaves out a line that has no reading at that time", () => {
    const readout = readoutFor(
      [
        { label: "read", samples: [] },
        { label: "write", samples: line(10, 20, 30, 40, 50) },
      ],
      window,
      0,
      percent,
      clock,
    );
    expect(readout?.text).toBe("t0 · write 10%");
  });

  it("is nothing when there are no samples", () => {
    expect(
      readoutFor([{ samples: [] }], window, 0.5, percent, clock),
    ).toBeNull();
  });
});

describe("the sample a key moves to", () => {
  const samples = line(10, 20, 30, 40, 50);

  it("takes the newest with the first arrow, whichever way", () => {
    expect(sampleAfterKey(samples, window, null, "ArrowLeft")).toBe(4000);
    expect(sampleAfterKey(samples, window, null, "ArrowRight")).toBe(4000);
  });

  it("moves one sample earlier or later from the one chosen", () => {
    expect(sampleAfterKey(samples, window, 2000, "ArrowLeft")).toBe(1000);
    expect(sampleAfterKey(samples, window, 2000, "ArrowRight")).toBe(3000);
  });

  it("stays on the oldest and the newest at the ends", () => {
    expect(sampleAfterKey(samples, window, 0, "ArrowLeft")).toBe(0);
    expect(sampleAfterKey(samples, window, 4000, "ArrowRight")).toBe(4000);
  });

  it("goes to the oldest with Home and the newest with End", () => {
    expect(sampleAfterKey(samples, window, 2000, "Home")).toBe(0);
    expect(sampleAfterKey(samples, window, 2000, "End")).toBe(4000);
  });

  it("counts only the samples inside the window", () => {
    const inside = { from: 1000, to: 3000 };

    expect(sampleAfterKey(samples, inside, null, "End")).toBe(3000);
    expect(sampleAfterKey(samples, inside, 1000, "ArrowLeft")).toBe(1000);
    expect(sampleAfterKey(samples, inside, 3000, "Home")).toBe(1000);
  });

  it("starts from the sample nearest the one it was on once the line has slid on", () => {
    expect(sampleAfterKey(samples, window, 2400, "ArrowRight")).toBe(3000);
  });

  it("is nothing with no sample in the window", () => {
    expect(sampleAfterKey([], window, null, "End")).toBeNull();
    expect(
      sampleAfterKey(samples, { from: 9000, to: 10000 }, null, "End"),
    ).toBeNull();
  });
});
