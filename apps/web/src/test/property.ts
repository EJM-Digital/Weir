import fc from "fast-check";

/** The seed every run uses unless `WEIR_PROPERTY_SEED` says otherwise. */
const FIXED_SEED = 20_261_015;

/** How many inputs a property is tried on unless `WEIR_PROPERTY_RUNS` says otherwise. */
const DEFAULT_RUNS = 100;

type Environment = Record<string, string | undefined>;

/** The variables the test process was started with; none where the runner has no process object. */
const environment: Environment =
  (globalThis as { process?: { env?: Environment } }).process?.env ?? {};

function chosenSeed(): number {
  const text = environment.WEIR_PROPERTY_SEED?.trim();
  if (!text) return FIXED_SEED;
  if (text.toLowerCase() === "random") {
    return Math.floor(Math.random() * 2 ** 31);
  }
  const seed = Number(text);
  if (!Number.isSafeInteger(seed)) {
    throw new Error(
      `WEIR_PROPERTY_SEED must be a whole number or "random", not "${text}".`,
    );
  }
  return seed;
}

function chosenRuns(): number {
  const runs = Number(environment.WEIR_PROPERTY_RUNS);
  return Number.isSafeInteger(runs) && runs > 0 ? runs : DEFAULT_RUNS;
}

/**
 * Tries a property on generated inputs with a seed that is the same on every run, so a pass or a failure can be repeated
 * exactly. A failure names the seed and the smallest input that fails; set `WEIR_PROPERTY_SEED` to that number to run the same
 * inputs again, or to `random` to look for new ones (the seed it picked is in the failure).
 */
export function assertProperty<Ts extends unknown[]>(
  property: fc.IProperty<Ts>,
): void {
  const seed = chosenSeed();
  try {
    fc.assert(property, { seed, numRuns: chosenRuns() });
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error);
    throw new Error(
      `Property failed with seed ${seed}. Run it again with WEIR_PROPERTY_SEED=${seed}.\n${detail}`,
      { cause: error },
    );
  }
}
