import { useState } from "react";

import {
  useUpdateSettingsMutation,
  useUpdateSettingsQuery,
  useUpdateStatusQuery,
} from "../../lib/settings/queries";

/**
 * Whether Weir checks for updates, asked in setup. Only the Windows app has the choice: a Docker or source install has no tray
 * to hold it, so nothing is asked there. The answer starts as what is saved (off on a new install) and is saved with the rest of setup.
 */
export function useWizardUpdates() {
  const status = useUpdateStatusQuery();
  const windows = status.data?.install_type === "windows";
  const saved = useUpdateSettingsQuery(windows);
  const save = useUpdateSettingsMutation();
  const [chosen, setChosen] = useState<boolean | null>(null);
  const settings = saved.data;

  return {
    /** Setup asks only where the choice exists, and once it is known what is saved. */
    asked: windows && settings !== undefined,
    checking: chosen ?? settings?.check_for_updates ?? false,
    choose: setChosen,
    /** Saves the answer when it differs from what is saved. */
    saveAnswer: async () => {
      if (settings === undefined || chosen === null) return;
      if (chosen === settings.check_for_updates) return;
      await save.mutateAsync({ ...settings, check_for_updates: chosen });
    },
  };
}
