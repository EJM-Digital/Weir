import { useRef } from "react";

import { ColumnsMenu } from "../../../../components/shared/columns-menu";
import { LoadError } from "../../../../components/shared/load-error";
import { Panel } from "../../../../components/panels/panel";
import type { useConfigurationBackupsQuery } from "../../../../lib/settings/queries";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { useTableColumns } from "../../../../lib/ui/use-table-columns";
import { BACKUP_COLUMNS } from "./backup-columns";
import { BackupTable } from "./backup-table";

/** How many snapshots Weir keeps; older ones are removed as new ones are taken. */
const SNAPSHOTS_KEPT = 5;

/**
 * The backups on this machine: what is in the folder, newest first, each one a download and a restore point. The
 * card's header holds what is done to the backups as a whole: making one now, downloading the settings as they are,
 * restoring from a file.
 */
export function BackupListSection({
  backupsQ,
  disabled,
  onBackUpNow,
  onDownloadSettings,
  onChooseFile,
  onDownload,
  onRestore,
  resultMessage,
  resultProblem,
}: {
  backupsQ: ReturnType<typeof useConfigurationBackupsQuery>;
  disabled: boolean;
  onBackUpNow: () => void;
  onDownloadSettings: () => void;
  onChooseFile: (file: File) => void;
  onDownload: (id: number, fileName: string) => void;
  onRestore: (id: number) => void;
  resultMessage: string | null;
  resultProblem: string | null;
}) {
  const columns = useTableColumns(BACKUP_COLUMNS);
  const fileInput = useRef<HTMLInputElement>(null);
  const items = backupsQ.data?.items ?? [];

  return (
    <Panel
      title="Backups"
      headingId="suite-settings-backup-snapshots-heading"
      padded
      count={`Keeps the latest ${SNAPSHOTS_KEPT}`}
      note={
        backupsQ.data ? (
          <span
            className="mm-sys-path"
            data-testid="suite-configuration-backup-directory"
          >
            {backupsQ.data.directory}
          </span>
        ) : undefined
      }
      aside={
        <>
          <button
            type="button"
            className={`${mmActionButtonClass({ variant: "primary" })} mm-sys-btn`}
            disabled={disabled}
            onClick={onBackUpNow}
          >
            Back up now
          </button>
          <button
            type="button"
            className={`${mmActionButtonClass({ variant: "secondary" })} mm-sys-btn`}
            disabled={disabled}
            title="Download every setting Weir would back up, as one file."
            onClick={onDownloadSettings}
          >
            Download settings
          </button>
          <button
            type="button"
            className={`${mmActionButtonClass({ variant: "secondary" })} mm-sys-btn`}
            disabled={disabled}
            title="Restore a Weir settings file from this computer."
            onClick={() => fileInput.current?.click()}
          >
            Restore from file…
          </button>
          <input
            ref={fileInput}
            type="file"
            accept="application/json,.json"
            className="hidden"
            aria-label="Choose configuration JSON file to restore"
            onChange={(e) => {
              const file = e.target.files?.[0];
              // Cleared first, so choosing the same file again still fires a change.
              e.target.value = "";
              if (file) onChooseFile(file);
            }}
          />
          {items.length > 0 ? <ColumnsMenu table={columns} /> : null}
        </>
      }
    >
      {backupsQ.isLoading ? (
        <p className="mm-quiet-note">Loading snapshot list...</p>
      ) : backupsQ.isError ? (
        <LoadError thing="your backups" error={backupsQ.error} />
      ) : items.length === 0 ? (
        <p className="mm-quiet-note">No automatic snapshots yet.</p>
      ) : (
        <BackupTable
          items={items}
          columns={columns}
          disabled={disabled}
          onDownload={onDownload}
          onRestore={onRestore}
        />
      )}
      {resultMessage ? (
        <p
          className="mm-status-text mm-sys-note"
          data-status="done"
          role="status"
        >
          {resultMessage}
        </p>
      ) : null}
      {resultProblem ? (
        <p
          className="mm-status-text mm-sys-note"
          data-status="broken"
          role="alert"
        >
          {resultProblem}
        </p>
      ) : null}
    </Panel>
  );
}
