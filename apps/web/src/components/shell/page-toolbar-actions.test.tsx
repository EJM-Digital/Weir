import { fireEvent, render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";

import { PageToolbar } from "./page-toolbar";
import {
  PageToolbarAction,
  PageToolbarActionsProvider,
  PageToolbarActionsSlot,
  PageToolbarAddButton,
} from "./page-toolbar-actions";

const TABS = [
  { id: "one", label: "One" },
  { id: "two", label: "Two" },
] as const;

/** The toolbar row: the tab list's landmark and the buttons' slot share it. */
const row = () =>
  screen.getByRole("navigation", { name: "Page tabs" })
    .parentElement as HTMLElement;

function renderRow(panel: ReactNode) {
  return render(
    <PageToolbarActionsProvider>
      <PageToolbar
        tabs={TABS}
        activeId="one"
        onSelect={vi.fn()}
        ariaLabel="Example sections"
        idPrefix="example-tab"
        panelId="example-panel"
        actions={<PageToolbarActionsSlot />}
      />
      <section data-testid="panel">{panel}</section>
    </PageToolbarActionsProvider>,
  );
}

describe("a tab's buttons in the toolbar row", () => {
  it("appear at the right of the row, not where the tab wrote them", () => {
    renderRow(
      <PageToolbarAction>
        <button type="button">Do it</button>
      </PageToolbarAction>,
    );

    const button = screen.getByRole("button", { name: "Do it" });
    expect(row()).toContainElement(button);
    expect(screen.getByTestId("panel")).not.toContainElement(button);
  });

  it("stay where they are written when there is no toolbar around them", () => {
    render(
      <section data-testid="panel">
        <PageToolbarAction>
          <button type="button">Do it</button>
        </PageToolbarAction>
      </section>,
    );

    expect(screen.getByTestId("panel")).toContainElement(
      screen.getByRole("button", { name: "Do it" }),
    );
  });

  it("go when the tab that wrote them goes", () => {
    const { rerender } = renderRow(
      <PageToolbarAction>
        <button type="button">Do it</button>
      </PageToolbarAction>,
    );

    rerender(
      <PageToolbarActionsProvider>
        <PageToolbar
          tabs={TABS}
          activeId="one"
          onSelect={vi.fn()}
          ariaLabel="Example sections"
          idPrefix="example-tab"
          panelId="example-panel"
          actions={<PageToolbarActionsSlot />}
        />
        <section data-testid="panel" />
      </PageToolbarActionsProvider>,
    );

    expect(screen.queryByRole("button", { name: "Do it" })).toBeNull();
  });

  it("keep the tab row's tabs beside them", () => {
    renderRow(null);

    expect(screen.getByRole("tab", { name: "One" })).toBeInTheDocument();
  });
});

describe("the add button", () => {
  it("is named for what it adds and runs its action when pressed", () => {
    const onClick = vi.fn();
    renderRow(<PageToolbarAddButton label="Add workflow" onClick={onClick} />);

    fireEvent.click(screen.getByRole("button", { name: "Add workflow" }));

    expect(onClick).toHaveBeenCalledOnce();
  });

  it("does nothing while it is disabled", () => {
    const onClick = vi.fn();
    renderRow(
      <PageToolbarAddButton label="New profile" disabled onClick={onClick} />,
    );

    const button = screen.getByRole("button", { name: "New profile" });
    fireEvent.click(button);

    expect(button).toBeDisabled();
    expect(onClick).not.toHaveBeenCalled();
  });

  it("refuses to draw the slot outside the provider, saying where it belongs", () => {
    vi.spyOn(console, "error").mockImplementation(() => undefined);

    expect(() => render(<PageToolbarActionsSlot />)).toThrow(
      "The toolbar's action slot belongs inside PageToolbarActionsProvider.",
    );
  });
});
