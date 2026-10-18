// A stand-in for a Weir with no account yet and for the browser that visits it, for testing what first-visit-account decides. It lands a visitor
// on account creation until an account exists and on sign-in after, and can be told to go wrong in the ways the scenario must catch.
// The real ones are the image under test and Chromium, in CI.

const USERNAME = "composer";

/**
 * @param {object} [behaviour]
 * @param {boolean} [behaviour.landsOnSignInFirst] The first visit lands on sign-in although there is no account.
 * @param {boolean} [behaviour.landsOnCreationAlways] A visit lands on account creation although the account exists.
 * @param {boolean} [behaviour.saysSessionExpired] The page says the session expired.
 * @param {boolean} [behaviour.refusesSignIn] The new account's password is not accepted.
 * @param {boolean} [behaviour.knowsWrongUser] Weir says it is someone else who signed in.
 * @param {string} [behaviour.warnsAtStart] A warning in the log from the start of the stack.
 */
export class FakeFreshWorld {
  visits = [];
  #account = null;
  #signedIn = false;
  #behaviour;

  constructor(behaviour = {}) {
    this.#behaviour = behaviour;
  }

  weir = {
    forgetSession: () => {
      this.#signedIn = false;
    },
    login: async (username, password) => {
      if (this.#behaviour.refusesSignIn || username !== this.#account?.username || password !== this.#account.password) throw new Error("signing in: expected HTTP 200, got 401");
      this.#signedIn = true;
    },
    get: async (path) => {
      if (path === "/auth/me") return { user: { username: this.#behaviour.knowsWrongUser ? "someone" : this.#account.username, role: "admin" } };
      if (path.startsWith("/system/log")) {
        const warning = { source: "server", id: 1, at: "t0", title: this.#behaviour.warnsAtStart };
        return { items: this.#signedIn && this.#behaviour.warnsAtStart ? [warning] : [], next_cursor: null };
      }
      throw new Error(`The stand-in has no route ${path}.`);
    },
  };

  browser = {
    visit: async (path) => {
      this.visits.push(path);
      const hasAccount = this.#account !== null;
      const signIn = hasAccount ? !this.#behaviour.landsOnCreationAlways : Boolean(this.#behaviour.landsOnSignInFirst);
      return { testIds: [signIn ? "login-username" : "setup-username", "app-root"], text: this.#behaviour.saysSessionExpired ? "Your session expired. Sign in again." : "Welcome to Weir" };
    },
  };

  async createAccount() {
    if (this.#account) throw new Error("a new volume must offer to create the first account");
    this.#account = { username: USERNAME, password: "a-password" };
    this.#signedIn = true;
    return this.#account;
  }

  asWorld() {
    return { weir: this.weir, browser: this.browser, createAccount: () => this.createAccount(), runId: "a1b2c3" };
  }
}
