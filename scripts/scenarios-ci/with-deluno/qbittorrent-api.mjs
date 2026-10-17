// qBittorrent's own Web API, signed in as the login this run gave it. Scenarios read what qBittorrent says, never what another application says
// qBittorrent did.
import { send } from "./http.mjs";

export class QbittorrentApi {
  #origin;
  #cookie;

  constructor(origin, cookie) {
    this.#origin = origin;
    this.#cookie = cookie;
  }

  static async signIn(origin, { username, password }) {
    const login = await send(origin, { method: "POST", path: "/api/v2/auth/login", form: { username, password }, what: "qBittorrent" });
    const cookie = login.headers.getSetCookie().map((header) => header.split(";")[0]).find((pair) => /^(QBT_)?SID(_\d+)?=/.test(pair));
    // A sign-in that worked is answered 200 "Ok." or, from newer versions, 204 with nothing; either way it sets the session cookie (named for the port from version 5).
    const said = login.text.trim();
    if (![200, 204].includes(login.status) || (said !== "" && !/^ok/i.test(said)) || !cookie) {
      throw new Error(`qBittorrent would not sign in: ${login.status} ${login.text.slice(0, 200)}`);
    }
    return new QbittorrentApi(origin, cookie);
  }

  async #request(method, path, form) {
    const answer = await send(this.#origin, { method, path, headers: { Cookie: this.#cookie }, form, what: "qBittorrent" });
    if (answer.status !== 200) throw new Error(`qBittorrent ${method} ${path} answered ${answer.status}: ${answer.text.slice(0, 300)}`);
    return answer.json ?? answer.text;
  }

  /** Where qBittorrent keeps unfinished and finished torrents, and with no DHT, PEX or local discovery, so a torrent only comes from where it is told. */
  setPreferences(preferences) {
    return this.#request("POST", "/api/v2/app/setPreferences", { json: JSON.stringify(preferences) });
  }

  categories() {
    return this.#request("GET", "/api/v2/torrents/categories");
  }

  createCategory(category, savePath) {
    return this.#request("POST", "/api/v2/torrents/createCategory", { category, savePath });
  }

  /** The torrent with this info hash, as qBittorrent itself lists it, or undefined. */
  async torrent(hash) {
    return (await this.#request("GET", `/api/v2/torrents/info?hashes=${hash}`))[0];
  }
}
