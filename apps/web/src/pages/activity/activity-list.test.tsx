import { render, screen, within } from "@testing-library/react";
import { expect, it, vi } from "vitest";

import type { ProcessingFile } from "../../lib/processing/files-api";
import type { LibraryClean } from "../../lib/processing/library-cleans-api";
import { WithWorkflows } from "../../test/with-workflows";
import { cleanEntry, downloadEntry } from "./activity-entries";
import { useTableColumns } from "../../lib/ui/use-table-columns";
import { ACTIVITY_COLUMNS } from "./activity-columns";
import { ActivityList } from "./activity-list";

function ListWithColumns(
  props: Omit<Parameters<typeof ActivityList>[0], "columns">,
) {
  const columns = useTableColumns(ACTIVITY_COLUMNS);
  return <ActivityList {...props} columns={columns} />;
}

const NOW = Date.parse("2026-10-02T10:00:00Z");

const download = {
  id: 1,
  library_id: 2,
  library_name: "TV",
  relative_path: "The.Quiet.Harbour.S01E01.1080p.WEB-DL.mkv",
  status: "processed",
  size_bytes: 1_000_000,
  created_at: "2026-10-02T09:50:00",
  updated_at: "2026-10-02T09:59:00",
  poster_url: "/api/v1/artwork/posters/tv-the-quiet-harbour-2019",
} as ProcessingFile;

const clean = {
  id: 7,
  library_id: 1,
  library_name: "Movies",
  relative_path: "D:/Media/Movies/Detour (1945)/Detour (1945).mkv",
  outcome: "cleaned",
  recorded_at: "2026-10-02T09:58:00",
} as LibraryClean;

function renderList() {
  render(
    <WithWorkflows>
      <ListWithColumns
        entries={[downloadEntry(download), cleanEntry(clean)]}
        selectedKey={null}
        now={NOW}
        onPick={vi.fn()}
      />
    </WithWorkflows>,
  );
}

it("says a file being worked on is being worked on until a percentage is known", () => {
  render(
    <WithWorkflows>
      <ListWithColumns
        entries={[
          downloadEntry({
            ...download,
            status: "processing",
            progress_percent: null,
          }),
        ]}
        selectedKey={null}
        now={NOW}
        onPick={vi.fn()}
      />
    </WithWorkflows>,
  );

  expect(screen.getByText("Working on it")).toBeInTheDocument();
});

it("says a cleaned copy no media manager has taken yet is waiting for it, and once imported, who imported it", () => {
  const copy = {
    output_path: "/hand-back/a.mkv",
    outcome: null,
    outcome_by: null,
  };
  const waiting = { ...download, id: 2, handback: copy } as ProcessingFile;
  const imported = {
    ...download,
    id: 3,
    handback: { ...copy, outcome: "imported", outcome_by: "Sonarr" },
  } as ProcessingFile;
  render(
    <WithWorkflows>
      <ListWithColumns
        entries={[
          downloadEntry(waiting, "linked"),
          downloadEntry(imported, "linked"),
          downloadEntry(waiting, "weir_only"),
        ]}
        selectedKey={null}
        now={NOW}
        onPick={vi.fn()}
      />
    </WithWorkflows>,
  );

  const [linked, importedRow, weirOnly] = screen.getAllByRole("row").slice(1);
  expect(
    within(linked).getByText("Waiting for your media manager"),
  ).toBeInTheDocument();
  expect(
    within(importedRow).getByText("Imported by Sonarr"),
  ).toBeInTheDocument();
  expect(within(weirOnly).getByText("Done")).toBeInTheDocument();
});

it("says a file the workflow's minimum size skipped was skipped, and one it removed was removed", () => {
  const extra = {
    ...download,
    id: 4,
    status: "skipped",
    skip_kind: "below_minimum_size",
    status_reason:
      "Skipped because this file is 12.1 MB, under the 50 MB minimum.",
  } as ProcessingFile;
  const removed = {
    ...extra,
    id: 6,
    skip_kind: "below_minimum_size_removed",
  } as ProcessingFile;
  const excluded = {
    ...download,
    id: 5,
    status: "skipped",
    status_reason:
      "Skipped because its path matches this workflow's exclude patterns.",
  } as ProcessingFile;
  render(
    <WithWorkflows>
      <ListWithColumns
        entries={[
          downloadEntry(extra),
          downloadEntry(removed),
          downloadEntry(excluded),
        ]}
        selectedKey={null}
        now={NOW}
        onPick={vi.fn()}
      />
    </WithWorkflows>,
  );

  const [kept, gone, other] = screen.getAllByRole("row").slice(1);
  expect(within(kept).getByText("Skipped")).toBeInTheDocument();
  expect(within(gone).getByText("Removed")).toBeInTheDocument();
  expect(within(other).getByText("Skipped")).toBeInTheDocument();
});

it("shows the title's small poster at the start of a download's row", () => {
  renderList();

  const [row] = screen.getAllByRole("row").slice(1);
  expect(within(row).getByRole("img")).toHaveAttribute(
    "src",
    "/api/v1/artwork/posters/tv-the-quiet-harbour-2019",
  );
});

it("shows the initials for a clean in a library, which has no poster of its own", () => {
  renderList();

  const [, row] = screen.getAllByRole("row").slice(1);
  expect(within(row).queryByRole("img")).toBeNull();
  expect(within(row).getByText("D")).toBeInTheDocument();
});

it("tints each row's tile for its workflow's place in Settings", () => {
  renderList();

  const [download, clean] = screen.getAllByRole("row").slice(1);
  expect(
    (download.querySelector(".mm-tile") as HTMLElement).style.getPropertyValue(
      "--tile-hue",
    ),
  ).toBe("265");
  expect(
    (clean.querySelector(".mm-tile") as HTMLElement).style.getPropertyValue(
      "--tile-hue",
    ),
  ).toBe("205");
});
