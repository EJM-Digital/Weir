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
  #allowed = new Map();

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
   * Names rows the scenario causes on purpose, such as the warning Weir writes for a sign-in it refuses: each `times` a row with this title
   * may appear, and no more. `why` says what in the scenario causes it.
   */
  expect(title, { times = 1, why }) {
    if (!why) throw new Error(`A row the scenario expects ("${title}") needs a reason.`);
    this.#allowed.set(title, (this.#allowed.get(title) ?? 0) + times);
  }

  /** Throws what the logs gained since `begin`, except the rows the scenario said it would cause. */
  async finish() {
    const page = await this.#weir.get(LOG_PAGE);
    const gained = newProblemRows(this.#before, page.items);
    if (page.next_cursor && gained.length >= page.items.length) {
      throw new Error("System > Logs gained more than one page of new warnings and errors, which is not a clean run.");
    }
    const left = new Map(this.#allowed);
    const unexpected = gained.filter((row) => {
      if (!(left.get(row.title) > 0)) return true;
      left.set(row.title, left.get(row.title) - 1);
      return false;
    });
    if (unexpected.length > 0) {
      throw new Error(`System > Logs gained ${unexpected.length} unexpected warning or error (${unexpected.map((row) => row.title).join(" | ")}).`);
    }
  }
}
