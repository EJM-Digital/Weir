import { useId, useState } from "react";
import { PageLoading } from "../../../../components/shared/page-loading";
import {
  NumberWithUnit,
  SettingRow,
  SettingsGroup,
} from "../../../../components/shared/settings-group";
import { MmOnOffSwitch } from "../../../../components/ui/mm-on-off-switch";
import { canEdit } from "../../../../lib/auth/can-edit";
import { useMeQuery } from "../../../../lib/auth/queries";
import {
  useProcessingFilesAtOnceQuery,
  useProcessingOperatorSettingsQuery,
  useProcessingOperatorSettingsSaveMutation,
} from "../../../../lib/processing/queries";
import type { ProcessingOperatorSettingsOut } from "../../../../lib/processing/types";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { errorMessage } from "../../../../lib/api/error-message";
import { SaveModelNote } from "../../save-model-note";
import { SettingsLoadError } from "../../settings-load-error";
import { useUnsavedChanges } from "../../unsaved-changes";

/** Files at once goes from one to ten (#633). */
const FILES_AT_ONCE = Array.from({ length: 10 }, (_, i) => i + 1);

/**
 * Setup › Performance: how hard Weir works. The cleanup switches are on Cleanup, each beside its own timer.
 */
export function ProcessSettingsSection() {
  const me = useMeQuery();
  const q = useProcessingOperatorSettingsQuery();

  if (q.isPending || me.isPending) {
    return <PageLoading label="Loading performance settings" />;
  }
  if (q.isError) {
    return <SettingsLoadError what="performance settings" />;
  }
  if (!q.data) return null;

  // Keyed by what this form edits, so it starts from the saved values and a change saved elsewhere replaces what is typed.
  const savedKey = [
    q.data.max_concurrent_files,
    q.data.runner_capacity,
    q.data.runner_cost_sd,
    q.data.runner_cost_720p,
    q.data.runner_cost_1080p,
    q.data.runner_cost_4k,
    q.data.runner_budget_enabled,
  ].join("|");
  return (
    <ProcessSettingsForm
      key={savedKey}
      saved={q.data}
      editable={canEdit(me.data?.role)}
    />
  );
}

function ProcessSettingsForm({
  saved,
  editable,
}: {
  saved: ProcessingOperatorSettingsOut;
  editable: boolean;
}) {
  const save = useProcessingOperatorSettingsSaveMutation();
  const filesAtOnce = useProcessingFilesAtOnceQuery();
  const ids = useId();

  const [maxConcurrentFiles, setMaxConcurrentFiles] = useState(
    saved.max_concurrent_files,
  );
  const [runnerCapacity, setRunnerCapacity] = useState(
    String(saved.runner_capacity),
  );
  const [runnerCostSd, setRunnerCostSd] = useState(
    String(saved.runner_cost_sd),
  );
  const [runnerCost720p, setRunnerCost720p] = useState(
    String(saved.runner_cost_720p),
  );
  const [runnerCost1080p, setRunnerCost1080p] = useState(
    String(saved.runner_cost_1080p),
  );
  const [runnerCost4k, setRunnerCost4k] = useState(
    String(saved.runner_cost_4k),
  );
  const [runnerBudgetEnabled, setRunnerBudgetEnabled] = useState(
    saved.runner_budget_enabled,
  );

  const int = (raw: string) => Number.parseInt(raw, 10);
  const costs = [
    int(runnerCapacity),
    int(runnerCostSd),
    int(runnerCost720p),
    int(runnerCost1080p),
    int(runnerCost4k),
  ];
  const valid =
    maxConcurrentFiles >= 1 &&
    maxConcurrentFiles <= FILES_AT_ONCE.length &&
    costs.every(
      (value, index) =>
        Number.isFinite(value) && value >= (index === 0 ? 1 : 0) && value <= 64,
    );
  const dirty =
    maxConcurrentFiles !== saved.max_concurrent_files ||
    runnerCapacity !== String(saved.runner_capacity) ||
    runnerCostSd !== String(saved.runner_cost_sd) ||
    runnerCost720p !== String(saved.runner_cost_720p) ||
    runnerCost1080p !== String(saved.runner_cost_1080p) ||
    runnerCost4k !== String(saved.runner_cost_4k) ||
    runnerBudgetEnabled !== saved.runner_budget_enabled;
  useUnsavedChanges(dirty ? "your speed settings" : null);
  const locked = !editable || save.isPending;

  const cost = (label: string, value: string, set: (v: string) => void) => (
    <SettingRow label={label} htmlFor={`${ids}-${label}`}>
      <NumberWithUnit
        id={`${ids}-${label}`}
        value={value}
        unit="units"
        min={0}
        max={64}
        disabled={locked}
        onChange={set}
      />
    </SettingRow>
  );

  const saveButton = (
    <>
      <button
        type="button"
        className={mmActionButtonClass({ variant: "primary" })}
        disabled={!editable || !valid || save.isPending}
        onClick={() =>
          save.mutate({
            max_concurrent_files: maxConcurrentFiles,
            runner_capacity: costs[0],
            runner_cost_sd: costs[1],
            runner_cost_720p: costs[2],
            runner_cost_1080p: costs[3],
            runner_cost_4k: costs[4],
            runner_budget_enabled: runnerBudgetEnabled,
          })
        }
      >
        {save.isPending ? "Saving…" : "Save performance settings"}
      </button>
      {save.isError ? (
        <span
          className="mm-status-text text-sm"
          data-status="broken"
          role="alert"
        >
          {errorMessage(save.error, "Save failed.")}
        </span>
      ) : null}
    </>
  );

  return (
    <div data-testid="processing-process-settings">
      <SettingsGroup
        title="How much at once"
        detail="More at once finishes a queue sooner but works the disks harder; past two or three, a slow disk gains little. A workflow can be held to fewer in its own settings."
        aside={<SaveModelNote model="explicit" />}
        footer={dirty || save.isError ? saveButton : undefined}
      >
        <SettingRow
          label="Files at once"
          hint={
            filesAtOnce.data ? (
              <>
                <span data-testid="processing-files-at-once-readout">
                  {filesAtOnce.data.running} running now.{" "}
                  {filesAtOnce.data.message || "Nothing is waiting."}
                </span>
                {filesAtOnce.data.slots_note ? (
                  <span
                    className="mt-1 block"
                    data-testid="processing-files-at-once-note"
                  >
                    {filesAtOnce.data.slots_note}
                  </span>
                ) : null}
              </>
            ) : undefined
          }
        >
          <div
            className="mm-choice-row"
            role="group"
            aria-label="Files at once"
          >
            {FILES_AT_ONCE.map((n) => (
              <button
                key={n}
                type="button"
                aria-pressed={n === maxConcurrentFiles}
                disabled={locked}
                onClick={() => setMaxConcurrentFiles(n)}
              >
                {n}
              </button>
            ))}
          </div>
        </SettingRow>
        <SettingRow
          label="Also weigh files by resolution"
          hint="For a machine that copes with several small files but only one 4K file. Off, the number above means exactly that."
        >
          <MmOnOffSwitch
            id={`${ids}-budget`}
            label="Also weigh files by resolution"
            enabled={runnerBudgetEnabled}
            disabled={locked}
            onChange={setRunnerBudgetEnabled}
            layout="control"
          />
        </SettingRow>
        {runnerBudgetEnabled ? (
          <>
            <SettingRow
              label="Budget"
              hint="A file starts only while its cost fits in what is left. Every file counts, however it arrives; one whose resolution is not known yet costs the same as a 1080p file."
              htmlFor={`${ids}-capacity`}
            >
              <NumberWithUnit
                id={`${ids}-capacity`}
                value={runnerCapacity}
                unit="units"
                min={1}
                max={64}
                disabled={locked}
                onChange={setRunnerCapacity}
              />
            </SettingRow>
            {cost("An SD file costs", runnerCostSd, setRunnerCostSd)}
            {cost("A 720p file costs", runnerCost720p, setRunnerCost720p)}
            {cost("A 1080p file costs", runnerCost1080p, setRunnerCost1080p)}
            {cost("A 4K file costs", runnerCost4k, setRunnerCost4k)}
          </>
        ) : null}
      </SettingsGroup>
    </div>
  );
}
