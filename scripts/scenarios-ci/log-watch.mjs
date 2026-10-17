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

  /** Throws what the logs gained since `begin`. */
  async finish() {
    const page = await this.#weir.get(LOG_PAGE);
    const gained = newProblemRows(this.#before, page.items);
    if (page.next_cursor && gained.length >= page.items.length) {
      throw new Error("System > Logs gained more than one page of new warnings and errors, which is not a clean run.");
    }
    if (gained.length > 0) {
      throw new Error(`System > Logs gained ${gained.length} unexpected warning or error (${gained.map((row) => row.title).join(" | ")}).`);
    }
  }
}
