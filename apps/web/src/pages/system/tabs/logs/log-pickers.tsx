import { useId } from "react";

import { StatusDot } from "../../../../components/panels/status-dot";
import { MmListboxPicker } from "../../../../components/ui/mm-listbox-picker";
import { MmMultiListboxPicker } from "../../../../components/ui/mm-multi-listbox-picker";
import { useProcessingLibrariesQuery } from "../../../../lib/processing/libraries-queries";
import {
  LOG_LEVEL_MEANING,
  type SystemLogCategory,
  type SystemLogPage,
} from "../../../../lib/system/system-log-api";
import {
  categoryHasRows,
  levelChoiceRows,
  workflowHasRows,
} from "./log-choices";
import {
  LOG_CATEGORY_LABELS,
  LOG_LEVEL_CHOICES,
  LOG_WHEN_OPTIONS,
  levelChoicesFor,
  levelsForChoices,
  type LogFilters,
  type LogWhen,
} from "./log-filters";

const CATEGORY_IDS = Object.keys(LOG_CATEGORY_LABELS) as SystemLogCategory[];

/** What the closed category picker says: nothing chosen is every category, one is named, several are counted. */
function categorySummary(chosen: readonly SystemLogCategory[]): string {
  if (chosen.length === 0) return "All categories";
  if (chosen.length === 1) return LOG_CATEGORY_LABELS[chosen[0]];
  return `${chosen.length} categories`;
}

/** What the closed level picker says: nothing chosen is every level, one is named, several are counted. */
function levelSummary(
  chosen: readonly (typeof LOG_LEVEL_CHOICES)[number][],
): string {
  if (chosen.length === 0) return "All levels";
  if (chosen.length === 1) return chosen[0].label;
  return `${chosen.length} levels`;
}

/**
 * The four pickers: what a row is about and how serious it is (each several at once, with how many rows each choice has),
 * its workflow, and how far back to read. They sit on the header's title line when it has room for them, and at the top of the Log card when it has not.
 * They offer only what the source and the other filters leave rows for, and the server log has no workflow to pick.
 */
export function LogPickers({
  filters,
  counts,
  busy,
  onChange,
}: {
  filters: LogFilters;
  /** What each choice would show, from the newest reading; undefined until there is one. */
  counts: SystemLogPage["counts"] | undefined;
  /** Whether the answer to the last choice is still on its way, so what they offer is the last one's. */
  busy: boolean;
  onChange: (next: Partial<LogFilters>) => void;
}) {
  const libraries = useProcessingLibrariesQuery();
  const categoryLabel = useId();
  const levelLabel = useId();
  const workflowLabel = useId();
  const whenLabel = useId();
  const categoryOptions = CATEGORY_IDS.filter(
    (id) => categoryHasRows(counts, id) || filters.categories.includes(id),
  ).map((id) => ({
    value: id,
    label:
      counts === undefined
        ? LOG_CATEGORY_LABELS[id]
        : `${LOG_CATEGORY_LABELS[id]} · ${counts.category[id].toLocaleString()}`,
  }));
  const chosenLevels = levelChoicesFor(filters.levels);
  const levelOptions = LOG_LEVEL_CHOICES.filter(
    (choice) =>
      counts === undefined ||
      levelChoiceRows(counts, choice) > 0 ||
      chosenLevels.includes(choice.value),
  ).map((choice) => ({
    value: choice.value,
    label:
      counts === undefined
        ? choice.label
        : `${choice.label} · ${levelChoiceRows(counts, choice).toLocaleString()}`,
    marker: <StatusDot meaning={LOG_LEVEL_MEANING[choice.levels[0]]} />,
  }));
  const workflowOptions = (libraries.data ?? []).filter(
    (library) =>
      workflowHasRows(counts, library.id) || library.id === filters.workflow,
  );
  return (
    <>
      <div className="mm-workflow-picker" aria-busy={busy}>
        <span id={categoryLabel} className="sr-only">
          Category
        </span>
        <MmMultiListboxPicker
          data-testid="logs-category-picker"
          options={categoryOptions}
          values={filters.categories}
          summaryText={categorySummary(filters.categories)}
          onChange={(next) =>
            onChange({ categories: next as SystemLogCategory[] })
          }
          ariaLabelledBy={categoryLabel}
        />
      </div>
      <div className="mm-workflow-picker" aria-busy={busy}>
        <span id={levelLabel} className="sr-only">
          Level
        </span>
        <MmMultiListboxPicker
          data-testid="logs-level-picker"
          options={levelOptions}
          values={chosenLevels}
          summaryText={levelSummary(
            LOG_LEVEL_CHOICES.filter((choice) =>
              chosenLevels.includes(choice.value),
            ),
          )}
          onChange={(next) => onChange({ levels: levelsForChoices(next) })}
          ariaLabelledBy={levelLabel}
        />
      </div>
      {filters.source === "server" ? null : (
        <div className="mm-workflow-picker" aria-busy={busy}>
          <span id={workflowLabel} className="sr-only">
            Workflow
          </span>
          <MmListboxPicker
            data-testid="logs-workflow-picker"
            options={[
              { value: "", label: "All workflows" },
              ...workflowOptions.map((library) => ({
                value: String(library.id),
                label: library.name,
              })),
            ]}
            value={filters.workflow === null ? "" : String(filters.workflow)}
            onChange={(next) =>
              onChange({ workflow: next === "" ? null : Number(next) })
            }
            ariaLabelledBy={workflowLabel}
          />
        </div>
      )}
      <div className="mm-workflow-picker mm-period-picker" aria-busy={busy}>
        <span id={whenLabel} className="sr-only">
          How far back
        </span>
        <MmListboxPicker
          data-testid="logs-when-picker"
          options={LOG_WHEN_OPTIONS}
          value={filters.when}
          onChange={(next) => onChange({ when: next as LogWhen })}
          ariaLabelledBy={whenLabel}
        />
      </div>
    </>
  );
}
