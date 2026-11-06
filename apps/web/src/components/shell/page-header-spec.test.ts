import { describe, expect, it } from "vitest";

import header from "../../styles/weir-header.css?raw";
import panels from "../../styles/weir-panels.css?raw";
import shelf from "../../styles/weir-shelf.css?raw";
import sidebar from "../../styles/weir-sidebar-nav.css?raw";
import sidebarBrand from "../../styles/weir-sidebar.css?raw";
import tokens from "../../styles/weir-tokens.css?raw";

/** The declarations of the first rule whose selector is exactly `selector`. */
function declarations(css: string, selector: string): Record<string, string> {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const rule = new RegExp(`(?:^|\\})\\s*${escaped}\\s*\\{([^}]*)\\}`).exec(
    css.replace(/\/\*[\s\S]*?\*\//g, ""),
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

  it("makes a panel header 48px high with its title box 12px below the panel top (1px border, 11px padding)", () => {
    const head = declarations(panels, ".mm-panel__head");
    expect(head["min-height"]).toBe("48px");
    expect(head["padding"]).toBe("11px 18px 14px");
  });

  it("sets a sidebar group label to 12px, 600, 19px line", () => {
    const label = declarations(sidebar, ".mm-sidebar-group__label");
    expect(label["font-size"]).toBe("12px");
    expect(label["font-weight"]).toBe("600");
    expect(label["line-height"]).toBe("19px");
  });

  it("sets the sidebar tagline under the app name to 12px on the sidebar's 1.52 line", () => {
    const tagline = declarations(sidebarBrand, ".mm-sidebar-brand__tagline");
    expect(tagline["font-size"]).toBe("12px");
    expect(tagline["line-height"]).toBeUndefined();
  });

  it("sets a panel header link to 13px, 500, 17px line", () => {
    const link = declarations(panels, ".mm-panel__link");
    expect(link["font-size"]).toBe("13px");
    expect(link["font-weight"]).toBe("500");
    expect(link["line-height"]).toBe("17px");
  });

  it("sets a panel header filter to the boxed switch: 30px, 13px/500 options in a 3px-padded, 2px-gap box, on one line", () => {
    const box = declarations(panels, ".mm-segmented");
    expect(box["padding"]).toBe("3px");
    expect(box["gap"]).toBe("2px");
    expect(box["border"]).toBe("1px solid var(--mm-hairline)");
    const option = declarations(panels, ".mm-segmented__option");
    expect(option["height"]).toBe("30px");
    expect(option["padding"]).toBe("0 14px");
    expect(option["font-size"]).toBe("13px");
    expect(option["font-weight"]).toBe("500");
    expect(shelf).not.toContain(".lsh-chip");
    expect(declarations(shelf, ".mm-shelf-filter .mm-segmented")).toMatchObject(
      { "flex-wrap": "nowrap", width: "max-content" },
    );
    // 30px option + 2 x 3px padding + 2 x 1px border = 38px; the controls' -8px margins leave 22px of the 23px line.
    expect(declarations(panels, ".mm-panel__controls")["margin"]).toBe(
      "-8px -8px",
    );
    expect(declarations(shelf, ".mm-shelf-filter")["padding"]).toBe("0 8px");
  });

  it("gives a panel title with controls beside it a 96px minimum width, so the controls start 12px after that", () => {
    const title = declarations(
      panels,
      ".mm-panel__head:has(> .mm-panel__controls) .mm-panel__title",
    );
    expect(title["min-width"]).toBe("96px");
    expect(declarations(panels, ".mm-panel__head")["gap"]).toBe("12px");
    expect(
      declarations(panels, ".mm-panel__title")["min-width"],
    ).toBeUndefined();
  });
});
