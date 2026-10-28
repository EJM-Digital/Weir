// Pulling an image a registry may throttle: a pinned digest is never at fault when the registry says "toomanyrequests", so ask again.
import { runForStatus } from "./shell.mjs";

const THROTTLED = /toomanyrequests/i;
/** How long to wait after each throttled attempt; the last throttled attempt fails the pull. */
export const THROTTLE_WAITS_MS = [15_000, 30_000, 60_000];

const sleepSync = (ms) => Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);

/**
 * Pulls `image`. Only a throttle is retried, up to `waitsMs.length` more times; any other failure, and a throttle that outlasts the waits,
 * throws with the registry's own words.
 * Each throttled attempt is logged, so a pull that gives up shows every refusal and how long it waited between them.
 * @param {{run?: typeof runForStatus, wait?: (ms: number) => void, waitsMs?: number[], log?: (line: string) => void}} [seams] Injected by the tests.
 */
export function pullImage(image, { run = runForStatus, wait = sleepSync, waitsMs = THROTTLE_WAITS_MS, log = console.log } = {}) {
  const attempts = waitsMs.length + 1;
  for (let attempt = 0; ; attempt += 1) {
    const result = run(`docker pull ${image}`);
    if (result.status === 0) return result.output;
    if (!THROTTLED.test(result.output) || attempt >= waitsMs.length) {
      throw new Error(`docker pull ${image} exited ${result.status}${attempt > 0 ? ` after ${attempt + 1} attempts` : ""}\n${result.output}`);
    }
    log(`docker pull ${image} was throttled (attempt ${attempt + 1} of ${attempts}), asking again in ${waitsMs[attempt]} ms: ${result.output.trim()}`);
    wait(waitsMs[attempt]);
  }
}
