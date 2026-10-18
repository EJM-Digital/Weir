import { useState } from "react";
import { Link } from "react-router-dom";

import { Panel } from "../../../../components/panels/panel";
import { PageLoading } from "../../../../components/shared/page-loading";
import { canEdit } from "../../../../lib/auth/can-edit";
import { useMeQuery } from "../../../../lib/auth/queries";
import type { ProcessingLibrary } from "../../../../lib/processing/libraries-api";
import { useProcessingLibrariesQuery } from "../../../../lib/processing/libraries-queries";
import { useAppSettingsQuery } from "../../../../lib/settings/queries";
import { useNow } from "../../../../lib/ui/use-now";
import { SaveModelNote } from "../../save-model-note";
import { SettingsLoadError } from "../../settings-load-error";
import { useLeaveConfirmation, useUnsavedChanges } from "../../unsaved-changes";
import { LibraryHoursEditor, LibraryHoursRow } from "./library-hours";
import { effectiveGrid, sameWeek } from "./schedule-model";

/** Often enough that "open until" and "it is 14:32 there" stay true while the page is open. */
const CLOCK_TICK_MS = 30_000;
const FALLBACK_ZONE = "UTC";

/** Where the time zone is chosen: every time on this tab is read in it. */
const TIME_ZONE_ADDRESS = "/system?tab=about";

function LibrariesSection({
  libraries,
  zone,
  now,
  editable,
}: {
  libraries: ProcessingLibrary[];
  zone: string;
  now: Date;
  editable: boolean;
}) {
  const [editing, setEditing] = useState<number | null>(null);
  const [grid, setGrid] = useState("");
  const ordered = [...libraries].sort(
    (x, y) => x.display_order - y.display_order,
  );
  const editingLibrary = ordered.find((l) => l.id === editing) ?? null;
  const dirty =
    editingLibrary !== null && !sameWeek(grid, effectiveGrid(editingLibrary));
  const thing = editingLibrary ? `${editingLibrary.name}'s hours` : null;
  useUnsavedChanges(dirty ? thing : null);
  const { confirmLeave, dialog } = useLeaveConfirmation();

  const openEditor = (library: ProcessingLibrary) => {
    setEditing(library.id);
    setGrid(effectiveGrid(library));
  };
  const closeEditor = () => setEditing(null);
  const requestClose = () => confirmLeave(dirty ? thing : null, closeEditor);
  const toggleEdit = (library: ProcessingLibrary) => {
    if (editing === library.id) requestClose();
    else confirmLeave(dirty ? thing : null, () => openEditor(library));
  };

  return (
    <Panel
      title="Weekly hours"
      count={
        <>
          When each workflow may start work, in {zone}.{" "}
          <Link className="mm-schedule-link" to={TIME_ZONE_ADDRESS}>
            Change the time zone
          </Link>
        </>
      }
      aside={<SaveModelNote model="explicit" />}
      padded
    >
      {ordered.length === 0 ? (
        <p className="text-sm text-mm-text3">
          Add a workflow under Workflows to schedule it here.
        </p>
      ) : (
        <div className="mm-quiet-table-wrap">
          <table className="mm-quiet-table mm-schedule-table">
            <thead>
              <tr>
                <th scope="col">Workflow</th>
                <th scope="col" title="Monday to Sunday, midnight to midnight">
                  Week
                </th>
                <th scope="col">Right now</th>
                <th scope="col">Looks for new files</th>
                <th scope="col">
                  <span className="sr-only">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {ordered.map((library) => (
                <LibraryHoursRow
                  key={library.id}
                  library={library}
                  zone={zone}
                  now={now}
                  editing={editing === library.id}
                  editable={editable}
                  onToggleEdit={() => toggleEdit(library)}
                />
              ))}
            </tbody>
          </table>
        </div>
      )}
      {editingLibrary ? (
        <LibraryHoursEditor
          key={editingLibrary.id}
          library={editingLibrary}
          grid={grid}
          onGrid={setGrid}
          editable={editable}
          onSaved={closeEditor}
          onClose={requestClose}
        />
      ) : null}
      {dialog}
    </Panel>
  );
}

/** Setup › Workflows › Schedule: a week per workflow with its hours editor below. */
export function ScheduleTab() {
  const me = useMeQuery();
  const settings = useAppSettingsQuery();
  const libraries = useProcessingLibrariesQuery();
  const now = new Date(useNow(CLOCK_TICK_MS));
  const editable = canEdit(me.data?.role);

  if (settings.isPending || libraries.isPending || me.isPending) {
    return <PageLoading label="Loading schedules" />;
  }
  if (settings.isError || libraries.isError) {
    return <SettingsLoadError what="schedules" />;
  }

  return (
    <div className="mm-quiet-stack" data-testid="processing-schedules-section">
      <LibrariesSection
        libraries={libraries.data}
        zone={settings.data.app_timezone || FALLBACK_ZONE}
        now={now}
        editable={editable}
      />
    </div>
  );
}
