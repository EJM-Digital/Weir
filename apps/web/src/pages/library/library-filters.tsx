import type {
  LibraryOverview,
  LibraryProblemKind,
  LibraryTotals,
} from "../../lib/processing/library-mode-api";
import { StatusDot } from "../../components/panels/status-dot";
import { HeaderSearch } from "../../components/shell/header-search";
import { MoreMenu } from "../../components/shell/more-menu";
import { foldedChipIndexes } from "../../components/shell/title-line-fit";
import type { StatusMeaning } from "../../lib/ui/status-meaning";
import {
  LIBRARY_STATUSES,
  PROBLEM_LABELS,
  STATUS_MEANING,
  type LibraryStatus,
} from "./library-model";

/** Which slice of the library the table shows: one status, or all of it. The reason list narrows "can't clean yet". */
export type LibraryFilter = {
  status: LibraryStatus | null;
  problem: LibraryProblemKind | null;
};

export const NO_FILTER: LibraryFilter = { status: null, problem: null };

/**
 * The status a link or a bookmark asks for, in `?show=`. Every address that has ever pointed at a slice of the library
 * still lands on it: the old words for the two statuses that were renamed are read as the new ones, and a slice that no
 * longer exists (what Weir has cleaned is history, not a status) shows everything. No parameter at all is "All".
 */
const LEGACY_SHOW: Record<string, LibraryStatus> = {
  would_change: "needs_cleaning",
  "would-change": "needs_cleaning",
  cannot_process: "cant_clean_yet",
  "cannot-process": "cant_clean_yet",
  "will-not-touch": "cant_clean_yet",
  untouched: "cant_clean_yet",
  "left-alone": "left_alone",
  "cant-clean-yet": "cant_clean_yet",
  "needs-cleaning": "needs_cleaning",
};

export function statusFromShow(value: string | null): LibraryStatus | null {
  if (!value) return null;
  const known = LIBRARY_STATUSES.find((status) => status === value);
  return known ?? LEGACY_SHOW[value] ?? null;
}

type Chip = {
  id: "all" | LibraryStatus;
  label: string;
  hint: string;
  /** What its files mean, which is what the same files mean in the table. "All" has none. */
  meaning?: StatusMeaning;
  count: (totals: LibraryTotals) => number;
};

/**
 * The chips, in the order a person reads them: every file, then one for each status, always, so the state of every file
 * is there at a glance; a status with no files shows 0, muted, and can still be chosen. Each file is in exactly one status,
 * so the five add up to All. The words are short; the tooltip says what each one means.
 */
const CHIPS: Chip[] = [
  {
    id: "all",
    label: "All",
    hint: "Every file in this workflow",
    count: (totals) => totals.files,
  },
  {
    id: "needs_cleaning",
    label: "Needs cleaning",
    hint: "Needs cleaning: these do not match your rules as they are now",
    meaning: STATUS_MEANING.needs_cleaning,
    count: (totals) => totals.by_status.needs_cleaning,
  },
  {
    id: "cleaning",
    label: "Cleaning",
    hint: "Cleaning: queued for a clean, or being cleaned right now",
    meaning: STATUS_MEANING.cleaning,
    count: (totals) => totals.by_status.cleaning,
  },
  {
    id: "matches",
    label: "Matches rules",
    hint: "Matches your rules: fine as they are",
    meaning: STATUS_MEANING.matches,
    count: (totals) => totals.by_status.matches,
  },
  {
    id: "cant_clean_yet",
    label: "Can't clean",
    hint: "Can't clean yet: still seeding, or Weir cannot read them. Weir tries again on its own",
    meaning: STATUS_MEANING.cant_clean_yet,
    count: (totals) => totals.by_status.cant_clean_yet,
  },
  {
    id: "left_alone",
    label: "Left alone",
    hint: "Left alone: you chose to skip these; nothing cleans them until you clear it",
    meaning: STATUS_MEANING.left_alone,
    count: (totals) => totals.by_status.left_alone,
  },
];

/** How many chips the row holds. */
export const LIBRARY_CHIP_COUNT = CHIPS.length;

const UNKNOWN_COUNT = "—";

/**
 * Narrows "can't clean yet" to one reason, in the Files card's header beside the chips' own counts: the reasons are
 * only about those files, so the list is there only while that status is chosen.
 */
export function LibraryReasonSelect({
  overview,
  filter,
  onFilter,
}: {
  overview: LibraryOverview | undefined;
  filter: LibraryFilter;
  onFilter: (filter: LibraryFilter) => void;
}) {
  const problems = overview?.problems ?? [];
  if (filter.status !== "cant_clean_yet" || problems.length === 0) return null;
  return (
    <label className="mm-library-problem">
      <span className="sr-only">Why Weir cannot clean a file yet</span>
      <select
        className="mm-input"
        value={filter.problem ?? ""}
        onChange={(event) => {
          const value = event.target.value as LibraryProblemKind | "";
          onFilter({ ...filter, problem: value === "" ? null : value });
        }}
      >
        <option value="">Any reason</option>
        {problems.map((group) => (
          <option key={group.kind} value={group.kind}>
            {PROBLEM_LABELS[group.kind]} ({group.files.toLocaleString()})
          </option>
        ))}
      </select>
    </label>
  );
}

/**
 * What narrows the Files table: the search, then the counts that are also the filters. They sit on the header's title
 * line after the library picker (see the library page's header), where the search gives up room first, then the last
 * chips fold into a "More" menu: every status stays reachable, whole, and nothing scrolls.
 */
export function LibraryFilters({
  search,
  onSearch,
  overview,
  filter,
  onFilter,
  fit,
  folded,
  onRow,
}: {
  search: string;
  onSearch: (value: string) => void;
  overview: LibraryOverview | undefined;
  filter: LibraryFilter;
  onFilter: (filter: LibraryFilter) => void;
  /** How many of the header's controls have given up their words (see useFitLevels): 1 shortens the search, 2 makes it a mark. */
  fit: number;
  /** How many chips, from the last, are folded into the "More" menu. */
  folded: number;
  /** Hands the page the chips' box, which is what it measures to fit the controls. */
  onRow: (row: HTMLDivElement | null) => void;
}) {
  const totals = overview?.totals;
  const isChosen = (chip: Chip) =>
    chip.id === "all" ? filter.status === null : filter.status === chip.id;
  const hidden = foldedChipIndexes(
    CHIPS.length,
    folded,
    CHIPS.findIndex(isChosen),
  );
  const countText = (chip: Chip) =>
    totals ? chip.count(totals).toLocaleString() : UNKNOWN_COUNT;
  const choose = (chip: Chip) =>
    onFilter({
      // A second click on the chosen one is the same as All; All is the way back.
      status:
        chip.id === "all" || isChosen(chip) ? null : (chip.id as LibraryStatus),
      problem: null,
    });
  const foldedChips = CHIPS.filter((_, index) => hidden.has(index));
  return (
    <>
      <HeaderSearch
        label="Search this workflow"
        placeholder="Search"
        className={`mm-library-search${fit >= 1 ? " mm-library-search--short" : ""}`}
        collapsed={fit >= 2}
        value={search}
        onChange={(event) => onSearch(event.target.value)}
      />
      <div className="mm-library-chips-box" ref={onRow}>
        <div className="mm-library-chips" role="group" aria-label="Show">
          {CHIPS.map((chip, index) => {
            if (hidden.has(index)) return null;
            const empty = totals && chip.count(totals) === 0;
            const meaning = empty ? "idle" : chip.meaning;
            return (
              <button
                key={chip.id}
                type="button"
                className="mm-library-chip"
                data-status={meaning}
                data-empty={empty ? "" : undefined}
                title={chip.hint}
                aria-pressed={isChosen(chip)}
                onClick={() => choose(chip)}
              >
                {meaning ? <StatusDot meaning={meaning} /> : null}
                {chip.label} <b>{countText(chip)}</b>
              </button>
            );
          })}
          {foldedChips.length > 0 ? (
            <MoreMenu
              menuLabel="More statuses"
              folded={foldedChips.map((chip) => ({
                id: chip.id,
                label: (
                  <>
                    {chip.label}{" "}
                    <span className="mm-segmented__count">
                      {countText(chip)}
                    </span>
                  </>
                ),
              }))}
              onChoose={(id) => {
                const chip = foldedChips.find(
                  (candidate) => candidate.id === id,
                );
                /* istanbul ignore else -- @preserve */
                if (chip) choose(chip);
              }}
              buttonClassName="mm-library-chip"
            />
          ) : null}
        </div>
      </div>
    </>
  );
}
