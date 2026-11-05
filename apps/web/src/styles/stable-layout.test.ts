import { describe, expect, it } from "vitest";

import connections from "./weir-connections.css?raw";
import header from "./weir-header.css?raw";
import panels from "./weir-panels.css?raw";
import side from "./weir-processing-side.css?raw";
import shell from "./weir-shell.css?raw";

/** The declarations of the first rule whose selector is exactly `selector`. */
function declarations(css: string, selector: string): Record<string, string> {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const rule = new RegExp(`(?:^|[}/])\\s*${escaped}\\s*\\{([^}]*)\\}`).exec(
    css,
  );
  if (!rule) throw new Error(`No rule for ${selector}`);
  const found: Record<string, string> = {};
  const body = rule[1].replace(/\/\*[\s\S]*?\*\//g, "");
  for (const line of body.split(";")) {
    const colon = line.indexOf(":");
    if (colon > 0)
      found[line.slice(0, colon).trim()] = line.slice(colon + 1).trim();
  }
  return found;
}

/**
 * A live update changes text in place and never the size of anything around it (Weir#1383). jsdom has no layout, so
 * these hold the CSS that keeps each of those promises.
 */
describe("what comes and goes does not move what is around it", () => {
  it("lays the shell's banners over the foot of the window, out of the page's flow", () => {
    const banners = declarations(shell, ".mm-banners");
    expect(banners.position).toBe("fixed");
    expect(banners.bottom).toBeDefined();
    expect(banners.top).toBeUndefined();
  });

  it("hangs the header's status from its lower edge instead of giving it room in the header", () => {
    const status = declarations(header, ".mm-header-status");
    expect(status.position).toBe("absolute");
    expect(status.top).toBe("100%");
  });

  it("lays the Needs you and Health notices over the foot of their panels", () => {
    for (const selector of [".mm-needs__notice", ".mm-health__notice"]) {
      const notice = declarations(side, selector);
      expect(notice.position, selector).toBe("absolute");
      expect(notice.bottom, selector).toBe("0");
    }
    expect(declarations(panels, ".mm-panel__body").position).toBe("relative");
  });

  it("gives a connection's time a reserved width, right-aligned and in tabular figures, so the role beside it stays put", () => {
    const when = declarations(connections, ".mm-conn__when");
    expect(when["min-width"]).toMatch(/^\d+ch$/);
    expect(when["text-align"]).toBe("right");
    expect(when["font-variant-numeric"]).toBe("tabular-nums");
    expect(when.flex).toBe("none");
  });
});
