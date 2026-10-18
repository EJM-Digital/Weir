import { useQueryClient } from "@tanstack/react-query";
import { useId, useState } from "react";

import { Panel } from "../../../../components/panels/panel";
import { ColumnsMenu } from "../../../../components/shared/columns-menu";
import { PageLoading } from "../../../../components/shared/page-loading";
import { useCanEdit } from "../../../../lib/auth/can-edit";
import type { MaintenanceFamily } from "../../../../lib/processing/maintenance-api";
import {
  useProcessingMaintenanceQuery,
  useRunProcessingMaintenance,
} from "../../../../lib/processing/maintenance-queries";
import { processingKeys } from "../../../../lib/processing/query-keys";
import {
  useProcessingOperatorSettingsQuery,
  useProcessingOperatorSettingsSaveMutation,
} from "../../../../lib/processing/queries";
import { plural } from "../../../../lib/ui/mm-plural";
import { useTableColumns } from "../../../../lib/ui/use-table-columns";
import { SaveModelNote } from "../../save-model-note";
import { SettingsLoadError } from "../../settings-load-error";
import {
  CleanupConfirmDialog,
  type CleanupConfirmAction,
} from "./cleanup-confirm-dialog";
import { CLEANUP_COLUMNS } from "./cleanup-columns";
import { CleanupJobsTable } from "./cleanup-jobs-table";
import type { CleanupJob } from "./cleanup-jobs";
import {
  CleanupJobRow,
  DaysSettingRow,
  SwitchSettingRow,
  type SaveSetting,
} from "./cleanup-rows";

/** The wait before an unclaimed hand-back copy may be removed. */
const WINDOW_MIN_DAYS = 1;
const WINDOW_MAX_DAYS = 365;
const WINDOW_DEFAULT_DAYS = 14;

/** Saves a setting, refreshes the jobs it changes, and says what happened. */
function useSaveSetting(onNotice: (notice: string | null) => void) {
  const save = useProcessingOperatorSettingsSaveMutation();
  const queryClient = useQueryClient();
  const saveSetting: SaveSetting = async (body, said) => {
    onNotice(null);
    try {
      await save.mutateAsync(body);
      void queryClient.invalidateQueries({
        queryKey: processingKeys.maintenance,
      });
      onNotice(said);
      return true;
    } catch {
      onNotice("That change could not be saved. Refresh and try again.");
      return false;
    }
  };
  return { saveSetting, saving: save.isPending };
}

/** Runs a job now for both kinds of library, so "Run now" means the whole job. */
function useRunNow(onNotice: (notice: string | null) => void) {
  const run = useRunProcessingMaintenance();
  const runNow = async (family: MaintenanceFamily, name: string) => {
    onNotice(null);
    try {
      const movies = await run.mutateAsync({ family, mediaScope: "movie" });
      const tv = await run.mutateAsync({ family, mediaScope: "tv" });
      onNotice(
        movies.queued || tv.queued
          ? `${name}: started. Its result shows under Last run.`
          : movies.detail,
      );
    } catch {
      onNotice(`${name} could not be started.`);
    }
  };
  return { runNow, running: run.isPending };
}

/**
 * Setup › Performance › Cleanup: the small jobs that keep Weir's folders tidy, each with its own
 * switch and timer, and the choice that decides what the leftover-work-file job leaves alone. A change applies within half a minute, with no restart: Weir's timers read the
 * switch and the interval again every 30 seconds.
 */
export function CleanupTab() {
  const editable = useCanEdit();
  const maintenance = useProcessingMaintenanceQuery();
  const settings = useProcessingOperatorSettingsQuery();
  const ids = useId();
  const columns = useTableColumns(CLEANUP_COLUMNS);
  const [notice, setNotice] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<{
    job: CleanupJob;
    action: CleanupConfirmAction;
  } | null>(null);
  const { saveSetting, saving } = useSaveSetting(setNotice);
  const { runNow, running } = useRunNow(setNotice);

  if (maintenance.isPending || settings.isPending) {
    return <PageLoading label="Loading cleanup settings" />;
  }
  if (maintenance.isError || settings.isError) {
    return <SettingsLoadError what="cleanup settings" />;
  }

  const families = maintenance.data.families;
  const shown = editable
    ? columns.order
    : columns.order.filter((id) => id !== "runNow");
  const columnCount = shown.length;
  const confirmingState = confirming
    ? families.find((f) => f.family === confirming.job.family)
    : undefined;

  const enableJob = (job: CleanupJob) =>
    void saveSetting(
      { [job.enabledField]: true },
      `${job.name} is on. It first runs within half a minute.`,
    );

  return (
    <div
      className="mm-quiet-stack"
      data-testid="processing-maintenance-section"
    >
      {families.length === 0 ? (
        <p className="mm-quiet-note">
          No cleanup jobs are available on this instance.
        </p>
      ) : (
        <Panel
          title="Jobs"
          count="Each runs on its own timer. A change applies within half a minute."
          aside={
            <>
              <ColumnsMenu table={columns} />
              <SaveModelNote model="instant" />
            </>
          }
          padded
        >
          {notice ? (
            <p
              className="mb-3 text-sm font-medium text-mm-text1"
              role="status"
              data-testid="processing-maintenance-notice"
            >
              {notice}
            </p>
          ) : null}
          <CleanupJobsTable
            columns={columns}
            shown={shown}
            states={families}
            row={({ job, state }) => (
              <CleanupJobRow
                job={job}
                state={state}
                order={shown}
                switchId={`${ids}-${job.family}-on`}
                editable={editable}
                saving={saving}
                running={running}
                onSave={saveSetting}
                onRun={() => void runNow(job.family, job.name)}
                onRequestConfirm={(action) => setConfirming({ job, action })}
              />
            )}
            after={(job) =>
              job.family === "work_temp_stale_sweep" ? (
                <SwitchSettingRow
                  testId="processing-maintenance-keep-failed-copy"
                  name="Keep a failed file’s half-written copy for a day, so you can look at it."
                  description="Leftover work files removes it once it is a day old."
                  columnCount={columnCount}
                  switchId={`${ids}-keep-failed`}
                  enabled={settings.data.keep_failed_work_files}
                  editable={editable}
                  saving={saving}
                  onChange={(on) =>
                    void saveSetting(
                      { keep_failed_work_files: on },
                      on
                        ? "A failed file’s half-written copy is now kept for a day."
                        : "A failed file’s half-written copy is no longer kept.",
                    )
                  }
                />
              ) : null
            }
            footer={
              <DaysSettingRow
                testId="processing-maintenance-handback-window"
                name="Cleaned copies nobody picked up wait"
                description="How long a copy Weir made for a media manager waits before Cleaned copies nobody picked up may remove it."
                columnCount={columnCount}
                inputId={`${ids}-window`}
                inputLabel="Cleaned copies nobody picked up wait for"
                unsavedThing="the days cleaned copies nobody picked up wait"
                saved={
                  settings.data.unclaimed_handback_window_days ??
                  WINDOW_DEFAULT_DAYS
                }
                min={WINDOW_MIN_DAYS}
                max={WINDOW_MAX_DAYS}
                editable={editable}
                saving={saving}
                savedWords={(days) =>
                  `Cleaned copies nobody picked up now wait ${plural(days, "day", "days")}.`
                }
                onSave={saveSetting}
                toBody={(days) => ({ unclaimed_handback_window_days: days })}
              />
            }
          />
        </Panel>
      )}

      {confirming && confirmingState ? (
        <CleanupConfirmDialog
          job={confirming.job}
          state={confirmingState}
          action={confirming.action}
          onCancel={() => setConfirming(null)}
          onConfirm={() => {
            const { job, action } = confirming;
            setConfirming(null);
            if (action === "enable") {
              enableJob(job);
            } else {
              void runNow(job.family, job.name);
            }
          }}
        />
      ) : null}
    </div>
  );
}
