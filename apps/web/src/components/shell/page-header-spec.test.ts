import { describe, expect, it } from "vitest";

import header from "../../styles/weir-header.css?raw";
import panels from "../../styles/weir-panels.css?raw";
import tokens from "../../styles/weir-tokens.css?raw";

/** The declarations of the first rule whose selector is exactly `selector`. */
function declarations(css: string, selector: string): Record<string, string> {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const rule = new RegExp(`(?:^|\\})\\s*${escaped}\\s*\\{([^}]*)\\}`).exec(css);
  if (!rule) throw new Error(`No rule for ${selector}`);
  const found: Record<string, string> = {};
  for (const line of rule[1].split(";")) {
    const colon = line.indexOf(":");
    if (colon > 0)
      found[line.slice(0, colon).trim()] = line.slice(colon + 1).trim();
  }
  return found;
}

/** The shared page-header spec (Weir#1359, Deluno#1461): exact pixels, none of them from the font scale. */
describe("page header matches the shared spec", () => {
  it("sets the page eyebrow to 11px, 700, uppercase, 0.14em, 14px line", () => {
    const eyebrow = declarations(header, ".mm-header__eyebrow");
    expect(eyebrow["font-size"]).toBe("11px");
    expect(eyebrow["font-weight"]).toBe("700");
    expect(eyebrow["letter-spacing"]).toBe("0.14em");
    expect(eyebrow["line-height"]).toBe("14px");
    expect(eyebrow["text-transform"]).toBe("uppercase");
  });

  it("sets the page title to 26px, 600, -0.03em", () => {
    const title = declarations(header, ".mm-header__title");
    expect(title["font-size"]).toBe("var(--mm-header-title-size)");
    expect(tokens).toMatch(/--mm-header-title-size:\s*26px;/);
    expect(title["font-weight"]).toBe("600");
    expect(title["letter-spacing"]).toBe("-0.03em");
  });

  it("sets a top-card label to 11px, 600, uppercase, 0.12em, 16px line", () => {
    const label = declarations(panels, ".mm-stat__label");
    expect(label["font-size"]).toBe("11px");
    expect(label["font-weight"]).toBe("600");
    expect(label["letter-spacing"]).toBe("0.12em");
    expect(label["line-height"]).toBe("16px");
    expect(label["text-transform"]).toBe("uppercase");
  });

  it("sets a panel title to 15px, 600, 23px line", () => {
    const title = declarations(panels, ".mm-panel__title");
    expect(title["font-size"]).toBe("var(--mm-type-panel)");
    expect(title["font-weight"]).toBe("600");
    expect(title["line-height"]).toBe("var(--panel-line)");
    expect(panels).toMatch(/--panel-line:\s*23px;/);
    expect(tokens).toMatch(/--mm-type-panel:\s*1rem;/);
  });
});
