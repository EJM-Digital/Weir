// Waiting for something the server does in its own time: ask again until it has happened or a ceiling is reached.

/**
 * Calls `check` until it returns a value other than undefined, waiting `intervalMs` between calls, and returns that value.
 * Throws with `what` once `ceilingMs` has passed without one. `clock` is injectable so a test needs no real time.
 * @template T
 * @param {() => Promise<T | undefined>} check
 */
export async function pollUntil(check, { what, ceilingMs, intervalMs, clock = { now: () => Date.now(), sleep: (ms) => new Promise((done) => setTimeout(done, ms)) } }) {
  const deadline = clock.now() + ceilingMs;
  for (;;) {
    const found = await check();
    if (found !== undefined) return found;
    if (clock.now() >= deadline) throw new Error(`${what} did not happen within ${ceilingMs / 1000} s.`);
    await clock.sleep(intervalMs);
  }
}
