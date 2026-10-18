// What the logs gained while a scenario ran. Every scenario ends with this: a run that left a warning or an error in System > Logs that was not
// there when it began has not passed, whatever else it showed.
const LOG_PAGE = "/system/log?level=warning,error&limit=100";

const key = (row) => `${row.source}|${row.id}|${row.at}|${row.title}`;

/** The rows in `after` that are not in `before`. */
export function newProblemRows(before, after) {
  const seen = new Set(before.map(key));
  return after.filter((row) => !seen.has(key(row)));
}

export class LogWatch {
  #weir;
  #before;

  constructor(weir, before) {
    this.#weir = weir;
    this.#before = before;
  }

  /** Notes the warnings and errors there are now. */
  static async begin(weir) {
    return new LogWatch(weir, (await weir.get(LOG_PAGE)).items);
  }

  /** Counts every warning and error there is when it finishes as new: for a stack that was made for the scenario, and could not be read before. */
  static sinceStart(weir) {
    return new LogWatch(weir, []);
  }

  /**
   * Throws what the logs gained since `begin`, except the rows `expected` accepts: a scenario that makes Weir fail on purpose expects the
   * warnings that say so. Returns the rows it accepted, so the scenario's verdict can say what it expected.
   */
  async finish(expected = () => false) {
    const page = await this.#weir.get(LOG_PAGE);
    const all = newProblemRows(this.#before, page.items);
    const accepted = all.filter(expected);
    const gained = all.filter((row) => !accepted.includes(row));
    if (page.next_cursor && gained.length >= page.items.length) {
      throw new Error("System > Logs gained more than one page of new warnings and errors, which is not a clean run.");
    }
    if (gained.length > 0) {
      throw new Error(`System > Logs gained ${gained.length} unexpected warning or error (${gained.map((row) => row.title).join(" | ")}).`);
    }
    return accepted;
  }
}
