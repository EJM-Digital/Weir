import { fireEvent, render, screen } from "@testing-library/react";
import { expect, it, vi } from "vitest";

import { LOG_COLUMNS } from "./log-columns";
import { LogRow } from "./log-row";
import { EVENT_ROW } from "./log-test-support";

function renderRow(expanded: boolean, onToggle = vi.fn()) {
  render(
    <ul>
      <LogRow
        row={EVENT_ROW}
        expanded={expanded}
        onToggle={onToggle}
        clock={() => "11:50:00"}
        fullTime={() => "2 Oct 2026 11:50:00"}
        order={LOG_COLUMNS.columns.map((column) => column.id)}
        actions={{
          canAct: true,
          onRelated: () => undefined,
          onJobChanged: () => undefined,
        }}
      />
    </ul>,
  );
  return onToggle;
}

// A real browser opens a button on Enter and Space by clicking it; the Logs end-to-end test presses the keys. What a
// keyboard needs from the row is that its head is that kind of button, in the tab order, and says whether it is open.
it("opens from a button in the tab order, so Tab, then Enter or Space, reaches it", () => {
  const onToggle = renderRow(false);

  const head = screen.getByRole("button", { name: /Sign-in finished/ });
  expect(head.tagName).toBe("BUTTON");
  expect(head).toHaveAttribute("type", "button");
  expect(head.tabIndex).toBe(0);
  expect(head).toHaveAttribute("aria-expanded", "false");
  expect(head).not.toHaveAttribute("aria-controls");

  head.focus();
  expect(head).toHaveFocus();
  fireEvent.click(head);
  expect(onToggle).toHaveBeenCalledTimes(1);
});

it("says it is open, and names the detail it opened", () => {
  renderRow(true);

  const head = screen.getByRole("button", { name: /Sign-in finished/ });
  expect(head).toHaveAttribute("aria-expanded", "true");
  const detailId = head.getAttribute("aria-controls");
  expect(detailId).toBe("log-row-event-41");
  expect(document.getElementById(detailId ?? "")).not.toBeNull();
});
