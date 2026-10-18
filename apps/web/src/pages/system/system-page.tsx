import { useState } from "react";
import { useSearchParams, type Location } from "react-router-dom";

import { PageLoading } from "../../components/shared/page-loading";
import {
  WorkspacePage,
  WorkspacePanel,
} from "../../components/shared/workspace-shell";
import type { PageTabOption } from "../../components/shell/page-tabs";
import { PageToolbar } from "../../components/shell/page-toolbar";
import { ConfirmDialog } from "../../components/ui/confirm-dialog";
import {
  isHttpErrorFromApi,
  isLikelyNetworkFailure,
} from "../../lib/api/error-guards";
import { canEdit } from "../../lib/auth/can-edit";
import { useMeQuery } from "../../lib/auth/queries";
import { useAppSettingsQuery } from "../../lib/settings/queries";
import { AboutTab } from "./tabs/about/about-tab";
import { BackupsTab } from "./tabs/backups/backups-tab";
import { LOG_PARAMS } from "./tabs/logs/log-filters";
import { LogsTab } from "./tabs/logs/logs-tab";
import { SecurityTab } from "./tabs/security/security-tab";
import { useSystemSettingsForm } from "./use-system-settings-form";
import { useUnsavedChangesGuard } from "./use-unsaved-changes-guard";

type TabId = "about" | "backups" | "security" | "logs";

/** What it is first, then what it keeps, who can sign in, and last the record of what it did. */
const SYSTEM_TABS: readonly PageTabOption<TabId>[] = [
  { id: "about", label: "About" },
  { id: "backups", label: "Backups" },
  { id: "security", label: "Security" },
  { id: "logs", label: "Logs" },
];

/** A tab name from the address, including older names, which land on the tab that took them in. */
function systemTabFrom(candidate: string | null | undefined): TabId {
  switch ((candidate || "").trim().toLowerCase()) {
    case "backups":
    case "backup":
      return "backups";
    case "security":
      return "security";
    case "logs":
    case "history":
      return "logs";
    default:
      return "about";
  }
}

/** The tab an address shows. */
function tabAt(location: Location): TabId {
  return systemTabFrom(new URLSearchParams(location.search).get("tab"));
}

function SettingsLoadProblem({ error }: { error: Error }) {
  return (
    <div className="mm-page" data-testid="suite-settings-page">
      <header className="mm-page__intro">
        <h2 className="mm-page__title">System</h2>
        <p className="mm-page__lead">
          {isLikelyNetworkFailure(error)
            ? "Could not reach the Weir API. Check that the backend is running."
            : isHttpErrorFromApi(error)
              ? "The server refused this request. Sign in again, then try back here."
              : "Something went wrong loading settings."}
        </p>
      </header>
    </div>
  );
}

/** System: Weir itself. What it is running, what it keeps, and who can sign in. */
export function SystemPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const me = useMeQuery();
  const settingsQ = useAppSettingsQuery();
  const form = useSystemSettingsForm(settingsQ.data);
  // The time zone is picked and saved on its own, so About tells the page when a pick is waiting for its Save.
  const [zoneUnsaved, setZoneUnsaved] = useState(false);
  // The settings drafts live in the page, so only another screen takes them away. A picked zone lives in About,
  // so leaving About for another tab takes it away too.
  const blocker = useUnsavedChangesGuard(
    form.isDirty || zoneUnsaved,
    (current, next) =>
      current.pathname !== next.pathname ||
      (zoneUnsaved && tabAt(current) !== tabAt(next)),
  );
  // The address is the tab, so Back, Forward and the side menu move between tabs too.
  const tab = systemTabFrom(searchParams.get("tab"));
  const editable = canEdit(me.data?.role);

  function selectTab(nextTab: TabId): void {
    const nextParams = new URLSearchParams(searchParams);
    // About is where System opens, so it needs no tab in the address.
    if (nextTab === "about") nextParams.delete("tab");
    else nextParams.set("tab", nextTab);
    // What narrows the log belongs to the log: another tab has none of it.
    for (const name of LOG_PARAMS) nextParams.delete(name);
    setSearchParams(nextParams);
  }

  if (settingsQ.isPending || me.isPending) {
    return <PageLoading label="Loading settings" />;
  }
  if (settingsQ.isError) {
    return <SettingsLoadProblem error={settingsQ.error} />;
  }
  const settings = settingsQ.data;

  return (
    <WorkspacePage dataTestId="suite-system-page">
      <PageToolbar
        tabs={SYSTEM_TABS}
        activeId={tab}
        onSelect={selectTab}
        ariaLabel="System sections"
        idPrefix="system-tab"
        panelId="system-panel"
        dataTestId="system-section-tabs"
      />
      <WorkspacePanel id="system-panel" labelledBy={`system-tab-${tab}`}>
        <div className="mm-system">
          {tab === "about" ? (
            <AboutTab
              settings={settings}
              editable={editable}
              onTimeZoneUnsavedChange={setZoneUnsaved}
            />
          ) : tab === "backups" ? (
            <BackupsTab form={form} editable={editable} settings={settings} />
          ) : tab === "security" ? (
            <SecurityTab />
          ) : (
            <LogsTab form={form} editable={editable} />
          )}
        </div>
      </WorkspacePanel>
      {blocker.state === "blocked" ? (
        <ConfirmDialog
          title="Leave without saving?"
          description="You have unsaved changes."
          confirmLabel="Leave without saving"
          cancelLabel="Stay here"
          testId="unsaved-changes-dialog"
          onCancel={() => blocker.reset()}
          onConfirm={() => blocker.proceed()}
        />
      ) : null}
    </WorkspacePage>
  );
}
