import { useState, type ReactNode } from "react";

import { Panel } from "../../../../components/panels/panel";
import { StatusDot } from "../../../../components/panels/status-dot";
import { ColumnsMenu } from "../../../../components/shared/columns-menu";
import { LoadError } from "../../../../components/shared/load-error";
import {
  eventLabel,
  eventOptions,
} from "../../../../lib/activity/activity-display";
import { useCanEdit } from "../../../../lib/auth/can-edit";
import type { SystemLogQuery } from "../../../../lib/system/system-log-api";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { plural } from "../../../../lib/ui/mm-plural";
import { useFitLevels } from "../../../../lib/ui/use-fit-levels";
import type { TableColumns } from "../../../../lib/ui/use-table-columns";
import { LogClearEvents } from "./log-clear-events";
import type { LogColumnId } from "./log-columns";
import { LogExportMenu } from "./log-export-menu";
import { anyLogFilterSet, refineCount, type LogFilters } from "./log-filters";
import { LogList } from "./log-list";
import type { LogRowActions } from "./log-row";
import { LogRange } from "./log-range";
import { LogRefine } from "./log-refine";
import { logSummaryWords } from "./log-summary";
import type { LogEntries } from "./use-log-entries";

/** The events in the list, as the options of the event type picker: those present, and the one chosen when it is not. */
function eventTypeOptions(
  entries: LogEntries,
  chosen: string,
): { value: string; label: string }[] {
  const options = eventOptions(
    entries.rows.flatMap((row) => (row.event ? [row.event] : [])),
  );
  return chosen && !options.some((option) => option.value === chosen)
    ? [...options, { value: chosen, label: eventLabel(chosen) }]
    : options;
}

/** Whether the card's header has run onto a second line: some control no longer shares the title's line. */
function headerWraps(head: HTMLElement): boolean {
  const first = head.firstElementChild?.getBoundingClientRect();
  return (
    first !== undefined &&
    Array.from(head.children).some(
      (child) => child.getBoundingClientRect().top >= first.bottom,
    )
  );
}

/**
 * The Log card: one list of everything that happened, newest first, under the day each thing fell on. Its header holds a
 * line on what the list holds, a way to refine it past what the header's filters can, Export, Clear events and the Log settings.
 */
export function LogCard({
  movedPickers,
  filters,
  query,
  columns,
  entries,
  watchFeed,
  expandedId,
  onToggle,
  onChange,
  onClearFilters,
  onOpenSettings,
  onRelated,
}: {
  /** The pickers, when the shell's header had no room for them; null while it holds them. They join this card's own header when it has the room, and a row above the list when not. */
  movedPickers: ReactNode;
  filters: LogFilters;
  query: SystemLogQuery;
  columns: TableColumns<LogColumnId>;
  entries: LogEntries;
  /** Hands the list's element over, so new rows can wait while the reader is further down. */
  watchFeed: (element: HTMLElement | null) => void;
  expandedId: string | null;
  onToggle: (id: string) => void;
  onChange: (next: Partial<LogFilters>) => void;
  onClearFilters: () => void;
  onOpenSettings: () => void;
  onRelated: (jobId: number) => void;
}) {
  const canClear = useCanEdit();
  const [refineOpen, setRefineOpen] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const refining = refineCount(filters);
  const filtered = anyLogFilterSet(filters);
  const problems = [problem, entries.olderError].filter(Boolean);
  const summary = logSummaryWords({
    loaded: entries.rows.length,
    total: entries.total,
    filtered,
  });
  const [summaryEl, setSummaryEl] = useState<HTMLElement | null>(null);
  const headerFit = useFitLevels(
    summaryEl?.closest("header") ?? null,
    1,
    `${summary}|${filtered}|${refining}|${canClear}|${movedPickers !== null}`,
    movedPickers !== null,
    headerWraps,
  );
  const pickersInRow = movedPickers !== null && headerFit >= 1;

  return (
    <>
      {problems.length > 0 ? (
        <ul
          className="mm-interrupt"
          data-status="broken"
          data-testid="logs-problems"
        >
          {problems.map((text) => (
            <li key={text} className="mm-interrupt__item">
              <span className="mm-interrupt__text" role="alert">
                {text}
              </span>
            </li>
          ))}
        </ul>
      ) : null}
      {notice ? (
        <p className="mm-quiet-note" role="status">
          {notice}
        </p>
      ) : null}
      <Panel
        title="Log"
        headingId="logs-heading"
        padded
        dataTestId="logs-card"
        count={
          <span
            className="mm-log-status"
            data-testid="log-summary"
            aria-busy={entries.loading}
            ref={setSummaryEl}
          >
            <StatusDot meaning="done" className="mm-log-live" />
            {summary}
          </span>
        }
        aside={
          <>
            {movedPickers !== null && !pickersInRow ? (
              <div className="mm-history-scope">{movedPickers}</div>
            ) : null}
            {filtered ? (
              <button
                type="button"
                className={`${mmActionButtonClass({ variant: "secondary" })} mm-sys-btn`}
                onClick={onClearFilters}
              >
                Clear filters
              </button>
            ) : null}
            <button
              type="button"
              className={`${mmActionButtonClass({ variant: "secondary" })} mm-sys-btn`}
              aria-expanded={refineOpen || refining > 0}
              onClick={() => setRefineOpen((open) => !open)}
            >
              {refining > 0 ? `Refine · ${refining}` : "Refine"}
            </button>
            <LogExportMenu query={query} onProblem={setProblem} />
            {canClear ? (
              <LogClearEvents
                onProblem={setProblem}
                onCleared={(done) => {
                  setNotice(done);
                  void entries.reload();
                }}
              />
            ) : null}
            <button
              type="button"
              className={`${mmActionButtonClass({ variant: "secondary" })} mm-sys-btn`}
              onClick={onOpenSettings}
            >
              Log settings
            </button>
            <ColumnsMenu table={columns} />
          </>
        }
      >
        {pickersInRow ? (
          <div className="mm-log-toolbar" data-testid="logs-toolbar">
            {movedPickers}
          </div>
        ) : null}
        {refineOpen || refining > 0 ? (
          <LogRefine
            filters={filters}
            eventTypes={eventTypeOptions(entries, filters.eventType)}
            onChange={onChange}
          />
        ) : null}
        {filters.when === "custom" ? (
          <LogRange from={filters.from} to={filters.to} onChange={onChange} />
        ) : null}
        <section
          ref={watchFeed}
          className="mm-log-feed"
          aria-busy={entries.loading}
        >
          {entries.arrived > 0 ? (
            <div className="sticky top-2 z-10 flex justify-center">
              <button
                type="button"
                className={mmActionButtonClass({ variant: "primary" })}
                onClick={entries.showArrived}
              >
                {`${plural(entries.arrived, "new entry", "new entries")} — show`}
              </button>
            </div>
          ) : null}
          <LogBody
            entries={entries}
            columns={columns}
            filtered={filtered}
            expandedId={expandedId}
            onToggle={onToggle}
            onClearFilters={onClearFilters}
            actions={{
              canAct: canClear,
              onRelated,
              onJobChanged: () => void entries.reload(),
            }}
          />
        </section>
        {entries.hasMore ? (
          <div className="mt-4">
            <button
              type="button"
              className="mm-quiet-link"
              disabled={entries.loadingMore}
              onClick={entries.loadMore}
            >
              {entries.loadingMore ? "Loading older…" : "Load older entries →"}
            </button>
          </div>
        ) : null}
      </Panel>
    </>
  );
}

/** The list, or what stands in its place: that it is loading, that it could not be read, or that nothing matches. */
function LogBody({
  entries,
  columns,
  filtered,
  expandedId,
  onToggle,
  onClearFilters,
  actions,
}: {
  entries: LogEntries;
  columns: TableColumns<LogColumnId>;
  filtered: boolean;
  expandedId: string | null;
  onToggle: (id: string) => void;
  onClearFilters: () => void;
  actions: LogRowActions;
}) {
  if (entries.error) return <LoadError thing="the log" error={entries.error} />;
  if (entries.rows.length === 0 && entries.loading) {
    return <p className="mm-quiet-note">Loading the log…</p>;
  }
  if (entries.rows.length === 0) {
    return (
      <div className="space-y-2 py-4" data-testid="log-empty">
        <p className="mm-quiet-note">
          {filtered
            ? "Nothing in the log matches these filters."
            : "Nothing has been logged yet."}
        </p>
        {filtered ? (
          <button
            type="button"
            className="mm-quiet-link"
            onClick={onClearFilters}
          >
            Clear filters →
          </button>
        ) : null}
      </div>
    );
  }
  return (
    <LogList
      rows={entries.rows}
      columns={columns}
      expandedId={expandedId}
      onToggle={onToggle}
      actions={actions}
    />
  );
}
