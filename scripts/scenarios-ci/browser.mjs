// A real browser for the scenarios that need to see a page as a person does: Chromium under Playwright, each visit in a browser context of its
// own, which is a brand-new profile with no cookies, no storage and no memory of an earlier visit.
import { pollUntil } from "../docker-smoke/poll.mjs";

// The page a visitor lands on once the app has decided who they are: creating the first account, or signing in.
const LANDING_PAGES = ["setup-username", "login-username"];
const LANDING_CEILING_MS = 30_000;

/** What the page shows right now: the test ids on it and its text. */
const SNAPSHOT = () => ({
  testIds: [...new Set([...document.querySelectorAll("[data-testid]")].map((element) => element.getAttribute("data-testid")))],
  text: document.body.innerText,
});

export class Browser {
  #browser;
  #origin;
  #clock;

  /** @param {{newContext(): Promise<object>, close(): Promise<void>}} browser A launched Playwright browser, or a stand-in for one. */
  constructor(browser, origin, clock) {
    this.#browser = browser;
    this.#origin = origin;
    this.#clock = clock;
  }

  static async launch(origin) {
    const { chromium } = await import("playwright-core");
    return new Browser(await chromium.launch(), origin);
  }

  /**
   * Opens `path` in a brand-new profile and returns what the page shows once the app has landed on account creation or sign-in, or what it
   * shows when thirty seconds have passed without either: the caller judges which page it is.
   * @returns {Promise<{testIds: string[], text: string}>}
   */
  async visit(path) {
    const context = await this.#browser.newContext();
    try {
      const page = await context.newPage();
      // Weir keeps a live connection to the page, so the network never goes idle: the wait is for the page to land, below.
      await page.goto(this.#origin + path, { waitUntil: "load" });
      let latest = { testIds: [], text: "" };
      const landed = pollUntil(
        async () => {
          latest = await page.evaluate(SNAPSHOT);
          return latest.testIds.some((id) => LANDING_PAGES.includes(id)) ? latest : undefined;
        },
        { what: "the app to land on a page", ceilingMs: LANDING_CEILING_MS, intervalMs: 500, clock: this.#clock },
      );
      return await landed.catch(() => latest);
    } finally {
      await context.close();
    }
  }

  async close() {
    await this.#browser.close();
  }
}
