import { render, screen } from "@testing-library/react";
import { expect, it } from "vitest";

import type { SystemLogRow } from "../../../../lib/system/system-log-api";
import { EVENT_ROW } from "../../../../test/log-test-support";
import { LOG_COLUMNS } from "./log-columns";
import { LogRow } from "./log-row";

function show(row: SystemLogRow, expanded = false) {
  render(
    <ul>
      <LogRow
        row={row}
        expanded={expanded}
        onToggle={() => undefined}
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
}

const PLAIN_ROW: SystemLogRow = {
  ...EVENT_ROW,
  id: "event:50",
  event: null,
  title: "Nothing more recorded",
  detail: "A line of its own",
};

it("shows the row's own title and detail when the source recorded no event, job or server line", () => {
  show(PLAIN_ROW, true);

  expect(screen.getByText("Nothing more recorded")).toBeInTheDocument();
  expect(screen.getByText("A line of its own")).toBeInTheDocument();
});

it("leaves the time empty when the row's time cannot be read", () => {
  show({ ...PLAIN_ROW, at: "not a time" });

  expect(screen.queryByText("11:50:00")).toBeNull();
});
