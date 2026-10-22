import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { PageTabs } from "./page-tabs";
import { PageToolbar } from "./page-toolbar";

const TABS = [
  { id: "overview", label: "Overview" },
  { id: "jobs", label: "Jobs" },
  { id: "logs", label: "Logs" },
] as const;

function renderTabs(activeId: string, onSelect: (id: string) => void) {
  render(
    <PageTabs
      tabs={TABS}
      activeId={activeId}
      onSelect={onSelect}
      ariaLabel="Example sections"
      idPrefix="example-tab"
      panelId="example-panel"
      placement="row"
    />,
  );
}

describe("page tabs", () => {
  it("is a tab list whose tabs name the panel they open", () => {
    renderTabs("overview", vi.fn());

    expect(
      screen.getByRole("tablist", { name: "Example sections" }),
    ).toHaveClass("mm-page-tabs--row");
    expect(screen.getByRole("tab", { name: "Overview" })).toHaveAttribute(
      "aria-controls",
      "example-panel",
    );
    expect(screen.getByRole("tab", { name: "Overview" })).toHaveAttribute(
      "aria-selected",
      "true",
    );
    expect(screen.getByRole("tab", { name: "Jobs" })).toHaveAttribute(
      "aria-selected",
      "false",
    );
  });

  it("chooses a tab on a click", () => {
    const onSelect = vi.fn();
    renderTabs("overview", onSelect);

    fireEvent.click(screen.getByRole("tab", { name: "Jobs" }));

    expect(onSelect).toHaveBeenCalledWith("jobs");
  });

  it("lets Tab reach only the chosen tab", () => {
    renderTabs("jobs", vi.fn());

    expect(screen.getByRole("tab", { name: "Jobs" })).toHaveAttribute(
      "tabindex",
      "0",
    );
    expect(screen.getByRole("tab", { name: "Overview" })).toHaveAttribute(
      "tabindex",
      "-1",
    );
  });

  it("moves to and chooses the next tab with the arrow keys, wrapping at the ends", () => {
    const onSelect = vi.fn();
    renderTabs("logs", onSelect);

    fireEvent.keyDown(screen.getByRole("tab", { name: "Logs" }), {
      key: "ArrowRight",
    });

    expect(onSelect).toHaveBeenLastCalledWith("overview");
    expect(screen.getByRole("tab", { name: "Overview" })).toHaveFocus();
  });

  it("jumps to the last tab with End and back to the first with Home", () => {
    const onSelect = vi.fn();
    renderTabs("overview", onSelect);
    const overview = screen.getByRole("tab", { name: "Overview" });

    fireEvent.keyDown(overview, { key: "End" });
    expect(onSelect).toHaveBeenLastCalledWith("logs");

    fireEvent.keyDown(overview, { key: "Home" });
    expect(onSelect).toHaveBeenLastCalledWith("overview");
  });
});

describe("page tabs, further keys", () => {
  it("moves back with ArrowLeft, wrapping from the first tab to the last", () => {
    const onSelect = vi.fn();
    renderTabs("overview", onSelect);

    fireEvent.keyDown(screen.getByRole("tab", { name: "Overview" }), {
      key: "ArrowLeft",
    });

    expect(onSelect).toHaveBeenLastCalledWith("logs");
    expect(screen.getByRole("tab", { name: "Logs" })).toHaveFocus();
  });

  it("chooses nothing for a key that is not for moving", () => {
    const onSelect = vi.fn();
    renderTabs("overview", onSelect);

    fireEvent.keyDown(screen.getByRole("tab", { name: "Overview" }), {
      key: "x",
    });

    expect(onSelect).not.toHaveBeenCalled();
  });

  it("lets Tab reach the first tab when none is chosen", () => {
    renderTabs("nothing-chosen", vi.fn());

    expect(screen.getByRole("tab", { name: "Overview" })).toHaveAttribute(
      "tabindex",
      "0",
    );
  });
});

describe("page toolbar", () => {
  it("names the landmark apart from the tab list inside it", () => {
    render(
      <PageToolbar
        tabs={TABS}
        activeId="overview"
        onSelect={vi.fn()}
        ariaLabel="Example sections"
        idPrefix="example-tab"
        panelId="example-panel"
      />,
    );

    expect(
      screen.getByRole("navigation", { name: "Page tabs" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("tablist", { name: "Example sections" }),
    ).toBeInTheDocument();
  });

  it("puts the page's own buttons beside the tabs", () => {
    render(
      <PageToolbar
        tabs={TABS}
        activeId="overview"
        onSelect={vi.fn()}
        ariaLabel="Example sections"
        idPrefix="example-tab"
        panelId="example-panel"
        actions={<button type="button">Reset to defaults</button>}
      />,
    );

    expect(
      screen.getByRole("button", { name: "Reset to defaults" }),
    ).toBeInTheDocument();
  });
});
