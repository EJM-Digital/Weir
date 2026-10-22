import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it } from "vitest";

import { ColumnsMenu } from "../../../../components/shared/columns-menu";
import type { MaintenanceFamilyState } from "../../../../lib/processing/maintenance-api";
import { useTableColumns } from "../../../../lib/ui/use-table-columns";
import { CLEANUP_COLUMNS } from "./cleanup-columns";
import { CleanupJobsTable } from "./cleanup-jobs-table";

function job(
  family: MaintenanceFamilyState["family"],
  over: Partial<MaintenanceFamilyState>,
): MaintenanceFamilyState {
  return {
    family,
    enabled: true,
    description: "",
    pending: 0,
    running: 0,
    last_completed_at: null,
    last_failed_at: null,
    last_error: null,
    interval_seconds: 3600,
    next_run_at: null,
    ...over,
  };
}

const STATES = [
  job("work_temp_stale_sweep", {
    enabled: false,
    interval_seconds: 600,
  }),
  job("unclaimed_handbacks", {
    enabled: true,
    interval_seconds: 21600,
  }),
];

function Jobs({
  editable = true,
  states = STATES,
}: {
  editable?: boolean;
  states?: MaintenanceFamilyState[];
}) {
  const columns = useTableColumns(CLEANUP_COLUMNS);
  const shown = editable
    ? columns.order
    : columns.order.filter((id) => id !== "runNow");
  return (
    <>
      <ColumnsMenu table={columns} />
      <CleanupJobsTable
        columns={columns}
        shown={shown}
        states={states}
        row={({ job: entry }) => (
          <tr>
            <th scope="row">{entry.name}</th>
          </tr>
        )}
        after={(entry) =>
          entry.family === "work_temp_stale_sweep" ? (
            <tr>
              <td>A setting of the leftover files job</td>
            </tr>
          ) : null
        }
        footer={
          <tr>
            <td>A setting of no job</td>
          </tr>
        }
      />
    </>
  );
}

function rows() {
  return screen
    .getAllByRole("row")
    .slice(1)
    .map((row) => row.textContent);
}

beforeEach(() => localStorage.clear());

describe("the cleanup jobs table", () => {
  it("lists the jobs in their own order, each with its setting under it, and the footer last", () => {
    render(<Jobs />);

    expect(rows()).toEqual([
      "Leftover work files",
      "A setting of the leftover files job",
      "Cleaned copies nobody picked up",
      "A setting of no job",
    ]);
  });

  it("moves a job's setting with the job when the jobs are sorted", () => {
    render(<Jobs />);

    fireEvent.click(screen.getByRole("button", { name: "On" }));

    expect(rows()).toEqual([
      "Cleaned copies nobody picked up",
      "Leftover work files",
      "A setting of the leftover files job",
      "A setting of no job",
    ]);
  });

  it("sorts by how often a job runs, shortest first", () => {
    render(<Jobs />);

    fireEvent.click(screen.getByRole("button", { name: "Every" }));
    expect(rows()[0]).toBe("Leftover work files");

    fireEvent.click(screen.getByRole("button", { name: "Every" }));
    expect(rows()[0]).toBe("Cleaned copies nobody picked up");
  });

  it("keeps the job column first and offers no Run now heading to a reader who cannot run a job", () => {
    render(<Jobs editable={false} />);

    const names = screen
      .getAllByRole("columnheader")
      .map((heading) => heading.textContent);
    expect(names[0]).toBe("Job");
    expect(names).not.toContain("Run now");
  });

  it("puts the jobs back in their own order with Reset columns", () => {
    render(<Jobs />);
    fireEvent.click(screen.getByRole("button", { name: "On" }));

    fireEvent.click(screen.getByRole("button", { name: "Columns" }));
    fireEvent.click(screen.getByRole("menuitem", { name: "Reset columns" }));

    expect(rows()[0]).toBe("Leftover work files");
  });

  it("sorts a job with no interval of its own as running hourly", () => {
    const withoutInterval = {
      ...job("work_temp_stale_sweep", {}),
      interval_seconds: undefined,
    };
    render(
      <Jobs
        states={[
          withoutInterval,
          job("unclaimed_handbacks", { interval_seconds: 7200 }),
        ]}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Every" }));

    expect(rows()[0]).toBe("Leftover work files");
  });

  it("sorts by the next run, with a job that is off last however it is sorted", () => {
    render(
      <Jobs
        states={[
          job("work_temp_stale_sweep", {
            enabled: false,
            next_run_at: "2026-10-02T09:00:00Z",
          }),
          job("unclaimed_handbacks", { next_run_at: "2026-10-02T12:00:00Z" }),
        ]}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Next run" }));

    expect(rows()[0]).toBe("Cleaned copies nobody picked up");
  });
});
