import type { SystemSettingsForm } from "../../use-system-settings-form";
import { LogSettingsPanel, useLogSettingsPanel } from "./log-settings-panel";
import { UnifiedLog } from "./unified-log";

/**
 * System › Logs: one log of everything Weir recorded. How long each kind of record is kept, and the server's counters,
 * are in the Log settings the Log card opens. A file's story is on Activity, not here.
 */
export function LogsTab({
  form,
  editable,
}: {
  form: SystemSettingsForm;
  editable: boolean;
}) {
  const settings = useLogSettingsPanel();
  return (
    <div className="mm-sys-stack" data-testid="settings-logs">
      <UnifiedLog onOpenSettings={settings.show} />
      <LogSettingsPanel
        open={settings.open}
        onClose={settings.close}
        form={form}
        editable={editable}
      />
    </div>
  );
}
