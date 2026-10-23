import {
  Navigate,
  Outlet,
  useBlocker,
  useLocation,
  useNavigate,
  type Location,
} from "react-router-dom";

import {
  WorkspacePage,
  WorkspacePanel,
} from "../../components/shared/workspace-shell";
import { PageToolbar } from "../../components/shell/page-toolbar";
import {
  PageToolbarActionsProvider,
  PageToolbarActionsSlot,
} from "../../components/shell/page-toolbar-actions";
import {
  setupAreaForPath,
  setupTabForPath,
  type SetupTabId,
} from "../../lib/settings/setup-areas";
import {
  LeaveWithoutSavingDialog,
  UnsavedChangesScope,
  useUnsavedChangesRegistry,
} from "./unsaved-changes";

/** The panel every tab of an area opens in, which the tab row names. */
const SETUP_PANEL_ID = "setup-panel";
const SETUP_TAB_PREFIX = "setup-tab";

/** A different address is a different tab or area, which unmounts the panel holding the edits; a query only changes what it shows. */
function leavesPanel(current: Location, next: Location): boolean {
  return current.pathname !== next.pathname;
}

/**
 * A setup area: the tab row under the shell's header, with the open tab's own buttons at its right, and the
 * open tab below. The side menu is the way between areas and the tabs are the way within one; the address
 * names both, so Back, Forward and a bookmark all land on the same tab. The header names the area.
 */
export function SetupAreaLayout() {
  const { pathname } = useLocation();
  const navigate = useNavigate();
  const { unsaved, register } = useUnsavedChangesRegistry();
  // Back, Forward, the side menu and the tab row are all navigation, so one blocker asks for all of them.
  const blocker = useBlocker(
    ({ currentLocation, nextLocation }) =>
      unsaved !== null && leavesPanel(currentLocation, nextLocation),
  );
  const area = setupAreaForPath(pathname);
  if (!area) return <Navigate to="/" replace />;
  const tab = setupTabForPath(area, pathname);

  const selectTab = (id: SetupTabId) => {
    const target = area.tabs.find((candidate) => candidate.id === id);
    /* istanbul ignore else -- @preserve */
    if (target) void navigate(target.path);
  };

  return (
    <WorkspacePage dataTestId="suite-settings-page">
      <PageToolbarActionsProvider>
        <PageToolbar
          tabs={area.tabs}
          activeId={tab.id}
          onSelect={selectTab}
          ariaLabel={`${area.label} sections`}
          idPrefix={SETUP_TAB_PREFIX}
          panelId={SETUP_PANEL_ID}
          actions={<PageToolbarActionsSlot />}
          dataTestId="setup-area-tabs"
        />
        <WorkspacePanel
          id={SETUP_PANEL_ID}
          labelledBy={`${SETUP_TAB_PREFIX}-${tab.id}`}
        >
          <UnsavedChangesScope register={register}>
            <Outlet />
          </UnsavedChangesScope>
        </WorkspacePanel>
      </PageToolbarActionsProvider>
      {blocker.state === "blocked" && unsaved !== null ? (
        <LeaveWithoutSavingDialog
          thing={unsaved}
          onStay={() => blocker.reset()}
          onLeave={() => blocker.proceed()}
        />
      ) : null}
    </WorkspacePage>
  );
}
