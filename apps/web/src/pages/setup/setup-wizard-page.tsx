import { useEffect, useMemo, useState } from "react";
import { Navigate } from "react-router-dom";

import { AuthBrandStack } from "../../components/brand/auth-brand-stack";
import { PageLoading } from "../../components/shared/page-loading";
import { MmListboxPicker } from "../../components/ui/mm-listbox-picker";
import { useMeQuery } from "../../lib/auth/queries";
import type { ProcessingLibrary } from "../../lib/processing/libraries-api";
import { useProcessingLibrariesQuery } from "../../lib/processing/libraries-queries";
import { useAppSettingsQuery } from "../../lib/settings/queries";
import { timezoneOptionsFor } from "../../lib/settings/timezone-options";
import type { AppSettings } from "../../lib/settings/types";
import { mmActionButtonClass } from "../../lib/ui/mm-control-roles";
import {
  BackupFields,
  DEFAULT_BACKUP_TIME,
  WizardLoadFailed,
  WizardSection,
  WizardWhatsNext,
} from "./setup-wizard-parts";
import { useSuggestedLibraries } from "./use-suggested-libraries";
import { useWizardConnections } from "./use-wizard-connections";
import {
  firstLibraryOfType,
  useWizardSave,
  type LibraryChoice,
  type LibraryFolders,
  type WizardDraft,
} from "./use-wizard-save";
import { WizardDownloadsSection } from "./wizard-downloads-section";
import { isConnectedSource, type DownloadSource } from "./wizard-source";

const FALLBACK_ZONE = "UTC";
const DEFAULT_BACKUP_HOURS = 24;

function wizardStateOf(settings: AppSettings | undefined): string {
  return (settings?.setup_wizard_state || "pending").trim().toLowerCase();
}

/** The draft starts from what is saved: the zone, the backup schedule and the first library of each type. */
function initialDraft(
  settings: AppSettings,
  libraries: ProcessingLibrary[] | undefined,
): WizardDraft {
  const zone = (settings.app_timezone || FALLBACK_ZONE).trim() || FALLBACK_ZONE;
  const folders = (type: "movie" | "tv"): LibraryFolders => {
    const library = firstLibraryOfType(libraries, type);
    return {
      watched: library?.watched_folder ?? "",
      output: library?.output_folder ?? "",
    };
  };
  return {
    timezone: zone,
    backup: {
      enabled: Boolean(settings.configuration_backup_enabled),
      intervalHours: String(
        settings.configuration_backup_interval_hours || DEFAULT_BACKUP_HOURS,
      ),
      preferredTime:
        (
          settings.configuration_backup_preferred_time || DEFAULT_BACKUP_TIME
        ).trim() || DEFAULT_BACKUP_TIME,
    },
    movie: folders("movie"),
    tv: folders("tv"),
  };
}

/** Someone with folders already set up is editing them; a new install has to answer the question first. */
function initialSource(
  libraries: ProcessingLibrary[] | undefined,
): DownloadSource | null {
  return (libraries ?? []).some(
    (library) => library.watched_folder || library.output_folder,
  )
    ? "neither"
    : null;
}

/** Leaving before the first run is finished or skipped asks the browser to confirm. */
function useLeaveWarning(active: boolean) {
  useEffect(() => {
    if (!active) return undefined;
    const handler = (event: BeforeUnloadEvent) => event.preventDefault();
    window.addEventListener("beforeunload", handler);
    return () => window.removeEventListener("beforeunload", handler);
  }, [active]);
}

/** Why setup cannot be finished as it stands, or null when it can. */
function finishBlocker(
  source: DownloadSource | null,
  answeringConnections: number,
  found: ReturnType<typeof useSuggestedLibraries>,
): string | null {
  if (source === null) {
    return "Choose how your downloads reach Weir first. “Neither” lets you pick the folders yourself.";
  }
  if (source === "neither") return null;
  if (answeringConnections === 0) {
    return "Connect and test it first, or choose “Neither” and pick the folders yourself.";
  }
  if (found.status.isLoading) {
    return "Weir is still asking what you connected for its folders. Try again in a moment.";
  }
  return found.blockedReason;
}

function WizardForm({
  settings,
  libraries,
}: {
  settings: AppSettings;
  libraries: ProcessingLibrary[] | undefined;
}) {
  const [draft, setDraft] = useState(() => initialDraft(settings, libraries));
  const [statusMessage, setStatusMessage] = useState<string | null>(null);
  const [finished, setFinished] = useState(false);
  const [source, setSource] = useState<DownloadSource | null>(() =>
    initialSource(libraries),
  );
  const connections = useWizardConnections(
    isConnectedSource(source) ? source : null,
  );
  const found = useSuggestedLibraries(connections.answering);
  const { finish, skip, pending } = useWizardSave({
    settings,
    libraries,
    onMessage: setStatusMessage,
    onFinished: () => setFinished(true),
  });
  const timezoneOptions = useMemo(
    () =>
      timezoneOptionsFor(settings.app_timezone).map((tz) => ({
        value: tz.id,
        label: tz.label,
      })),
    [settings.app_timezone],
  );
  const setFolders = (key: "movie" | "tv", folders: LibraryFolders) =>
    setDraft((current) => ({ ...current, [key]: folders }));

  const choice: LibraryChoice =
    source === "neither"
      ? { kind: "typed" }
      : { kind: "offered", plan: found.plan };
  const blocker = finishBlocker(source, connections.answering.length, found);
  const connectedInSetup =
    isConnectedSource(source) && connections.answering.length > 0;

  if (finished) {
    return <WizardWhatsNext connected={connectedInSetup ? source : null} />;
  }

  return (
    <main className="mm-auth-body" id="mm-main-content" tabIndex={-1}>
      <div className="mm-auth-frame mm-setup-wizard-frame">
        <AuthBrandStack />
        <div className="mm-auth-card mm-setup-wizard-card">
          <p className="mm-auth-eyebrow">First run</p>
          <h1 className="mm-auth-title">Set up Weir</h1>
          <p className="mm-auth-lead">
            A few basics to get Weir going. Everything here can be changed later
            in Setup, and you can skip it for now.
          </p>

          <div className="mm-quiet-stack mt-5">
            <WizardSection
              headingId="setup-wizard-libraries-heading"
              title="Downloads and workflows"
              description="Weir connects to whatever delivers your downloads, then starts your workflows from the folders it reports. Nothing is created until you finish."
            >
              <WizardDownloadsSection
                source={source}
                onSource={setSource}
                connections={connections}
                found={found}
                draft={draft}
                onFolders={setFolders}
                disabled={pending}
              />
            </WizardSection>

            <WizardSection
              headingId="setup-wizard-basics-heading"
              title="Basics"
              description="The clock Weir uses for schedules and for everything it writes down."
            >
              <div className="mm-wizard-basics">
                <div className="min-w-0">
                  <span id="setup-wizard-timezone" className="mm-wizard-label">
                    Time zone
                  </span>
                  <MmListboxPicker
                    ariaLabelledBy="setup-wizard-timezone"
                    placeholder="Select time zone"
                    disabled={pending}
                    options={timezoneOptions}
                    value={draft.timezone}
                    onChange={(timezone) =>
                      setDraft((current) => ({ ...current, timezone }))
                    }
                  />
                </div>
              </div>
            </WizardSection>

            <WizardSection
              headingId="setup-wizard-backups-heading"
              title="Automatic backups"
              description="Keep a rolling local copy of your Weir configuration."
            >
              <BackupFields
                draft={draft.backup}
                onChange={(backup) =>
                  setDraft((current) => ({ ...current, backup }))
                }
              />
            </WizardSection>
          </div>

          {statusMessage ? (
            // The gap goes on a wrapper: `.mm-auth-banner` sets its own margin, which
            // beats an `mt-4` on the banner itself and leaves it flush.
            <div className="mt-4">
              <p className="mm-auth-banner" data-status="broken" role="alert">
                {statusMessage}
              </p>
            </div>
          ) : null}

          <div className="mt-4 flex flex-wrap gap-3">
            <button
              type="button"
              className="mm-auth-submit"
              onClick={() => {
                if (blocker) {
                  setStatusMessage(blocker);
                  return;
                }
                void finish(draft, choice);
              }}
              disabled={pending}
            >
              {pending
                ? "Saving…"
                : wizardStateOf(settings) === "pending"
                  ? "Finish setup"
                  : "Save changes"}
            </button>
            <button
              type="button"
              data-testid="setup-wizard-skip"
              className={mmActionButtonClass({ variant: "secondary" })}
              onClick={() => void skip()}
              disabled={pending}
            >
              Skip for now
            </button>
          </div>
        </div>
      </div>
    </main>
  );
}

export function SetupWizardPage() {
  const me = useMeQuery();
  const settingsQ = useAppSettingsQuery();
  const processingQ = useProcessingLibrariesQuery();
  const loading = me.isPending || settingsQ.isPending || processingQ.isPending;
  useLeaveWarning(!loading && wizardStateOf(settingsQ.data) === "pending");

  if (loading) return <PageLoading label="Loading setup wizard" />;
  if (!me.data) return <Navigate to="/login" replace />;
  if (settingsQ.isError || !settingsQ.data) return <WizardLoadFailed />;
  return <WizardForm settings={settingsQ.data} libraries={processingQ.data} />;
}
