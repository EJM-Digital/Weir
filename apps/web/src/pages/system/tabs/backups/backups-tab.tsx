import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";

import { ConfirmDialog } from "../../../../components/ui/confirm-dialog";
import { useConfigurationBackupsQuery } from "../../../../lib/settings/queries";
import { settingsKeys } from "../../../../lib/settings/query-keys";
import type { AppSettings } from "../../../../lib/settings/types";
import type { SystemSettingsForm } from "../../use-system-settings-form";
import { BackupListSection } from "./backup-list-section";
import { BackupScheduleSection } from "./backup-schedule-section";
import { useBackupActions } from "./use-backup-actions";

/** System › Backups: when Weir backs up on the left, the backups on this machine on the right, as tall as each other. */
export function BackupsTab({
  form,
  editable,
  settings,
}: {
  form: SystemSettingsForm;
  editable: boolean;
  settings: AppSettings;
}) {
  const queryClient = useQueryClient();
  const backupsQ = useConfigurationBackupsQuery(editable);
  const actions = useBackupActions();
  const blocked = actions.busy || form.save.isPending;
  // The schedule's own failure already shows beside its Save button (form.save.isError); this is
  // only the success line, so it can sit right there too instead of in a shared banner.
  const [scheduleSaved, setScheduleSaved] = useState(false);

  const saveSchedule = () => {
    setScheduleSaved(false);
    form.saveFrom("backup", {
      onSaved: () => {
        setScheduleSaved(true);
        void queryClient.invalidateQueries({
          queryKey: settingsKeys.configurationBackups,
        });
      },
      onFailed: () => setScheduleSaved(false),
    });
  };

  return (
    <div data-testid="suite-settings-backup-tab">
      {editable ? (
        <div
          className="mm-sys-grid mm-sys-grid--backups"
          data-testid="suite-settings-backup-restore"
        >
          <BackupScheduleSection
            form={form}
            settings={settings}
            backups={backupsQ.data?.items}
            onSave={saveSchedule}
            saved={scheduleSaved}
          />
          <BackupListSection
            backupsQ={backupsQ}
            disabled={blocked}
            onBackUpNow={() => void actions.backUpNow()}
            onDownloadSettings={() => void actions.downloadConfiguration()}
            onChooseFile={(file) => void actions.chooseRestoreFile(file)}
            onDownload={(id, fileName) =>
              void actions.downloadSnapshot(id, fileName)
            }
            onRestore={(id) => void actions.chooseSavedBackup(id)}
            resultMessage={actions.result.message}
            resultProblem={actions.result.problem}
          />
        </div>
      ) : null}
      {actions.pendingRestore ? (
        <ConfirmDialog
          title="Replace the settings on this server with this backup?"
          description="This cannot be undone."
          confirmLabel="Replace settings"
          cancelLabel="Keep current settings"
          tone="danger"
          testId="restore-configuration-dialog"
          onCancel={actions.cancelRestore}
          onConfirm={actions.confirmRestore}
        />
      ) : null}
    </div>
  );
}
