import { render, screen } from "@testing-library/react";
import { useEffect, useState } from "react";
import { expect, it } from "vitest";

/** Longer than the 1 second a wait is given by default. */
const STALL_MS = 1200;

/** Holds the thread still for `ms`, the way a machine that is out of memory or starved of CPU does, without using any. */
function stallFor(ms: number): void {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}

function ShownSoonAfterAStall() {
  const [shown, setShown] = useState(false);
  useEffect(() => {
    const stall = setTimeout(() => stallFor(STALL_MS), 5);
    const show = setTimeout(() => setShown(true), 10);
    return () => {
      clearTimeout(stall);
      clearTimeout(show);
    };
  }, []);
  return shown ? <p>shown</p> : null;
}

it("waits for the page through a stall of the whole machine, because a wait is cut off only by a hang guard", async () => {
  render(<ShownSoonAfterAStall />);

  expect(await screen.findByText("shown")).toBeInTheDocument();
});
