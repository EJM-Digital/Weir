import { useEffect, useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";

import { SidePanel } from "../../../../components/shared/side-panel";
import type { SystemSettingsForm } from "../../use-system-settings-form";
import { RetentionSection } from "./retention-section";
import { ServerDiagnostics } from "./server-diagnostics";
import { useFileActivityRetention } from "./use-file-activity-retention";

/** What an address's #fragment says to open the Log settings, as it did when how long things are kept sat at the foot of the page. */
const LOG_SETTINGS_ANCHOR = "retention";

/** The address of the Log settings, for the pages whose records they govern. */
export const RETENTION_PATH = `/system?tab=logs#${LOG_SETTINGS_ANCHOR}`;

/** Whether the Log settings are open: closed unless the address asks, and opened by the Log card's button. */
export function useLogSettingsPanel() {
  const { pathname, search, hash } = useLocation();
  const navigate = useNavigate();
  const linked = hash === `#${LOG_SETTINGS_ANCHOR}`;
  const [open, setOpen] = useState(linked);
  useEffect(() => {
    if (linked) setOpen(true);
  }, [linked]);

  return {
    open,
    show: () => setOpen(true),
    close: () => {
      setOpen(false);
      if (linked) void navigate({ pathname, search }, { replace: true });
    },
  };
}

/**
 * System › Logs' settings in a slide-over from the Log card: how long each kind of record is kept, and the server's
 * counters for troubleshooting. The page behind it is only the log.
 */
export function LogSettingsPanel({
  open,
  onClose,
  form,
  editable,
}: {
  open: boolean;
  onClose: () => void;
  form: SystemSettingsForm;
  editable: boolean;
}) {
  const fileActivity = useFileActivityRetention();
  return (
    <SidePanel
      open={open}
      title="Log settings"
      eyebrow="System · Logs"
      onClose={onClose}
      dataTestId="log-settings-panel"
    >
      <RetentionSection
        form={form}
        fileActivity={fileActivity}
        editable={editable}
      />
      <ServerDiagnostics />
    </SidePanel>
  );
}
