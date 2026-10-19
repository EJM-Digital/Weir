// The recording proxy's control port, as the scenarios read it: everything that has passed between Weir and Deluno, in order.
import { send } from "./http.mjs";

export class ExchangeLog {
  #origin;

  constructor(origin) {
    this.#origin = origin;
  }

  /** Every exchange so far, oldest first. */
  async all() {
    const answer = await send(this.#origin, { method: "GET", path: "/exchanges?after=0", what: "The recording proxy" });
    if (answer.status !== 200 || !Array.isArray(answer.json?.exchanges)) throw new Error(`The recording proxy's list answered ${answer.status}: ${answer.text.slice(0, 300)}`);
    return answer.json.exchanges;
  }

  /** The exchanges of one route (`to-deluno` or `to-weir`) that satisfy a test, oldest first. */
  async where(route, test = () => true) {
    return (await this.all()).filter((exchange) => exchange.route === route && test(exchange));
  }
}
