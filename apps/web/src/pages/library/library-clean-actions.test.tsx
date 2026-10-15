import { fireEvent, render, screen } from "@testing-library/react";
import { expect, it, vi } from "vitest";

import { LibraryCleanActions } from "./library-clean-actions";
import type { LibraryClean } from "./use-library-clean";

function flowThatIs(pending: boolean): LibraryClean {
  return {
    isPending: (source: string) => pending && source === "selection",
    failure: () => null,
    outcome: null,
    start: vi.fn(),
  } as unknown as LibraryClean;
}

function bar(flow: LibraryClean) {
  render(
    <LibraryCleanActions
      selected={new Set(["/films/a.mkv"])}
      selectedSaving={0}
      known={[]}
      onClearSelection={vi.fn()}
      flow={flow}
    />,
  );
}

it("offers to clean the selection while nothing is being checked", () => {
  bar(flowThatIs(false));

  expect(
    screen.getByRole("button", { name: "Clean these files" }),
  ).toBeEnabled();
});

it("says Checking these files… and cannot be pressed again while the server works out what a clean would remove", () => {
  const flow = flowThatIs(true);
  bar(flow);

  const checking = screen.getByRole("button", {
    name: "Checking these files…",
  });
  expect(checking).toBeDisabled();
  fireEvent.click(checking);
  expect(flow.start).not.toHaveBeenCalled();
});
