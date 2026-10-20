import { render, screen } from "@testing-library/react";
import { useEffect, useState } from "react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";

// A test file takes its stand-ins for browser globals (matchMedia, fetch) away in an afterEach. A page still up at that
// point runs what it had not yet run against the real ones, which jsdom lacks or the setup file refuses, and the test
// fails. How long the page stays up past that point depends on the machine, so this holds the line open for a turn of the
// event loop, as a busy one does, and the page must be down before the stand-ins go.
const STAND_IN = "weirUnmountBeforeUnstubStandIn";
const STAND_IN_VALUE = "present";
const standInSeenByTheEffect: Array<unknown> = [];

/** A page whose effect, after an update that waits for the event loop, needs the stand-in: Activity's detail asks matchMedia this way when a file is picked. */
function Page() {
  const [picked, setPicked] = useState(false);
  useEffect(() => {
    if (picked) {
      standInSeenByTheEffect.push(
        (globalThis as Record<string, unknown>)[STAND_IN],
      );
    }
  }, [picked]);
  return (
    <button
      type="button"
      onClick={() => void Promise.resolve().then(() => setPicked(true))}
    >
      pick
    </button>
  );
}

beforeEach(() => {
  vi.stubGlobal(STAND_IN, STAND_IN_VALUE);
});

afterEach(async () => {
  vi.unstubAllGlobals();
  await new Promise((resolve) => setImmediate(resolve));
});

it("ends a test the moment after a click, before the page has run the effect the click set off", () => {
  render(<Page />);

  screen.getByText("pick").click();
});

it("never ran that effect once the stand-in was gone", () => {
  expect(standInSeenByTheEffect).not.toContain(undefined);
});
