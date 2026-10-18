import { render, screen, within } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { PageToolbar, PageToolbarButtons } from "./page-toolbar";
import {
  ShellHeaderProvider,
  useHeaderButtonsSlotRef,
  useHeaderTabsSlotRef,
} from "./shell-header-context";

const TABS = [
  { id: "overview", label: "Overview" },
  { id: "jobs", label: "Jobs" },
] as const;

/** The shell's header, reduced to the places its tabs and its page buttons go. */
function TitleLine() {
  const tabsRef = useHeaderTabsSlotRef();
  const buttonsRef = useHeaderButtonsSlotRef();
  return (
    <div data-testid="title-line">
      <div ref={tabsRef} />
      <div data-testid="title-line-buttons" ref={buttonsRef} />
    </div>
  );
}

function stubWindowWidth(wide: boolean) {
  vi.stubGlobal("matchMedia", (query: string) => ({
    matches: wide && query.includes("min-width: 1024px"),
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  }));
}

function renderInShell(actions?: ReactNode) {
  return render(
    <ShellHeaderProvider>
      <TitleLine />
      <PageToolbar
        tabs={TABS}
        activeId="overview"
        onSelect={vi.fn()}
        ariaLabel="Example sections"
        idPrefix="example-tab"
        panelId="example-panel"
        actions={actions}
        dataTestId="example-tabs"
      />
    </ShellHeaderProvider>,
  );
}

afterEach(() => vi.unstubAllGlobals());

describe("where the header has room", () => {
  it("puts the tabs on the title line, as the tab list that carries the test id", () => {
    stubWindowWidth(true);
    renderInShell();

    const titleLine = screen.getByTestId("title-line");
    const tabs = within(titleLine).getByRole("tablist", {
      name: "Example sections",
    });
    expect(tabs).toHaveAttribute("data-testid", "example-tabs");
    expect(tabs).toHaveClass("mm-page-tabs--title");
  });

  it("has no row under the title", () => {
    stubWindowWidth(true);
    const { container } = renderInShell();

    expect(container.querySelector(".mm-page-toolbar")).toBeNull();
    expect(screen.queryByRole("navigation", { name: "Page tabs" })).toBeNull();
  });

  it("puts the page's buttons at the right of the title line, and draws no row of their own", () => {
    stubWindowWidth(true);
    const { container } = renderInShell(
      <button type="button">Add workflow</button>,
    );

    expect(
      within(screen.getByTestId("title-line-buttons")).getByRole("button", {
        name: "Add workflow",
      }),
    ).toBeInTheDocument();
    expect(container.querySelector(".mm-page-toolbar")).toBeNull();
  });
});

describe("where the header is too narrow", () => {
  it("leaves the header and puts the tabs in the page's own row, beside its buttons", () => {
    stubWindowWidth(false);
    renderInShell(<button type="button">Add workflow</button>);

    expect(
      within(screen.getByTestId("title-line")).queryByRole("tablist"),
    ).toBeNull();
    const row = screen.getByRole("navigation", { name: "Page tabs" })
      .parentElement as HTMLElement;
    expect(
      within(row).getByRole("tablist", { name: "Example sections" }),
    ).toHaveClass("mm-page-tabs--row");
    expect(
      within(row).getByRole("button", { name: "Add workflow" }),
    ).toBeInTheDocument();
  });

  it("is where the tabs stay when the window cannot say how wide it is", () => {
    renderInShell();

    expect(
      screen.getByRole("navigation", { name: "Page tabs" }),
    ).toBeInTheDocument();
  });
});

describe("a page's own buttons when it has no tabs", () => {
  function renderButtons() {
    return render(
      <ShellHeaderProvider>
        <TitleLine />
        <PageToolbarButtons>
          <button type="button">Check again</button>
          <button type="button">Set up</button>
        </PageToolbarButtons>
      </ShellHeaderProvider>,
    );
  }

  it("sit at the right of the title line where the header has room, with no row of their own", () => {
    stubWindowWidth(true);
    const { container } = renderButtons();

    const line = within(screen.getByTestId("title-line-buttons"));
    expect(line.getByRole("button", { name: "Check again" })).toBeVisible();
    expect(line.getByRole("button", { name: "Set up" })).toBeVisible();
    expect(container.querySelector(".mm-page-toolbar")).toBeNull();
  });

  it("sit in a row of the page's own, at its right, where the header is too narrow", () => {
    stubWindowWidth(false);
    const { container } = renderButtons();

    const row = container.querySelector(".mm-page-toolbar__actions");
    expect(row).not.toBeNull();
    expect(within(row as HTMLElement).getAllByRole("button")).toHaveLength(2);
    expect(
      within(screen.getByTestId("title-line-buttons")).queryByRole("button"),
    ).toBeNull();
  });
});
