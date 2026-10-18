import { Navigate, Outlet, useLocation } from "react-router-dom";
import { PageLoading } from "../components/shared/page-loading";
import { SetupNeedsAdmin } from "../pages/setup/setup-needs-admin";
import { canEdit } from "../lib/auth/can-edit";
import { useMeQuery } from "../lib/auth/queries";
import { useAppSettingsQuery } from "../lib/settings/queries";

/**
 * Until the first run is finished or skipped, everyone who can change settings is sent to the wizard.
 * Someone who cannot (a viewer) is told an administrator has to finish it, because the wizard is not theirs.
 */
export function RequireSetupWizard() {
  const location = useLocation();
  const role = useMeQuery().data?.role;
  const settingsQ = useAppSettingsQuery();

  if (settingsQ.isPending) {
    return <PageLoading label="Loading setup" />;
  }
  if (settingsQ.isError || !settingsQ.data) {
    return <Outlet />;
  }

  const wizardState = (settingsQ.data.setup_wizard_state || "pending")
    .trim()
    .toLowerCase();
  if (wizardState === "pending") {
    if (!canEdit(role)) return <SetupNeedsAdmin />;
    if (location.pathname !== "/setup-wizard") {
      return <Navigate to="/setup-wizard" replace />;
    }
  }

  return <Outlet />;
}
