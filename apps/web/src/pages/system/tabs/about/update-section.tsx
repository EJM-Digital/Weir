import { useState } from "react";

import { Panel } from "../../../../components/panels/panel";
import { errorMessage } from "../../../../lib/api/error-message";
import { useUpdateStatusQuery } from "../../../../lib/settings/queries";
import type { UpdateStatus } from "../../../../lib/settings/types";
import { useAppDateFormatter } from "../../../../lib/ui/mm-format-date";
import { UpdateActions } from "./update-actions";
import { UpdatePreferences } from "./update-preferences";
import { UpdateStatusLine } from "./update-status-line";

const COPIED_MS = 2000;

function CopyCommandButton({ command }: { command: string }) {
  const [copied, setCopied] = useState(false);
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(command);
      setCopied(true);
      window.setTimeout(() => setCopied(false), COPIED_MS);
    } catch {
      setCopied(false);
    }
  };
  return (
    <button
      type="button"
      className="mm-quiet-link"
      onClick={() => void copy()}
      aria-label="Copy the update command"
    >
      {copied ? "Copied" : "Copy →"}
    </button>
  );
}

/** While GitHub is limiting the checks, the newest release Weir had learned of before that. */
function LastKnownRelease({ status }: { status: UpdateStatus }) {
  const formatDate = useAppDateFormatter();
  if (status.status !== "rate_limited" || !status.latest_version) return null;
  return (
    <p
      className="mm-quiet-note"
      data-testid="suite-settings-last-known-release"
    >
      Latest known: {status.latest_version}
      {status.published_at
        ? `, published ${formatDate(status.published_at)}`
        : ""}
    </p>
  );
}

function ReleaseStatus({
  status,
  checking,
  isWindows,
  dockerCommand,
  onCheck,
}: {
  status: UpdateStatus;
  checking: boolean;
  isWindows: boolean;
  /** The command a Docker install runs to take the update, when there is an update and it is one. */
  dockerCommand: string | null;
  onCheck: () => void;
}) {
  const links = [
    { label: "Download installer →", href: status.windows_installer_url },
    { label: "Release notes →", href: status.release_url },
  ]
    .filter((link) => link.href)
    .map((link) => (
      <a
        key={link.label}
        className="mm-quiet-link"
        href={link.href ?? undefined}
        target="_blank"
        rel="noreferrer"
      >
        {link.label}
      </a>
    ));

  return (
    <Panel
      title="Updates"
      headingId="suite-settings-upgrade-heading"
      headingLevel={3}
      padded
      dataTestId="suite-settings-upgrade-tab"
      aside={
        isWindows ? undefined : (
          <>
            <button
              type="button"
              className="mm-quiet-link"
              disabled={checking}
              onClick={onCheck}
            >
              {checking ? "Checking..." : "Check again →"}
            </button>
            {links}
          </>
        )
      }
    >
      <UpdateStatusLine status={status} isWindows={isWindows} />
      <LastKnownRelease status={status} />
      {isWindows ? (
        <>
          <UpdateActions status={status} />
          <p className="mm-update-links">{links}</p>
        </>
      ) : null}
      {isWindows ? <UpdatePreferences /> : null}
      {dockerCommand ? <DockerUpgradeSteps command={dockerCommand} /> : null}
    </Panel>
  );
}

/** How a Docker install takes the update it has been told about: the command to run, and what to keep. */
function DockerUpgradeSteps({ command }: { command: string }) {
  return (
    <section
      className="mm-update-next"
      aria-labelledby="suite-settings-upgrade-docker-heading"
    >
      <div className="mm-update-next__head">
        <h4
          id="suite-settings-upgrade-docker-heading"
          className="mm-update-next__title"
        >
          What happens next
        </h4>
        <CopyCommandButton command={command} />
      </div>
      <p className="mm-update-command">{command}</p>
      <p className="mm-sys-note">
        Keep the same WEIR_HOME volume and WEIR_SESSION_SECRET value across
        upgrades so browser sessions and setup state continue cleanly.
      </p>
    </section>
  );
}

/** Which Weir is installed, whether a newer one exists, and how this install takes updates. */
export function UpdateSection() {
  const updateStatusQ = useUpdateStatusQuery();
  const status = updateStatusQ.data;
  const isWindows = status?.install_type === "windows";
  const updateAvailable =
    status?.status === "update_available" ||
    status?.known_update_available === true;

  if (updateStatusQ.isPending) {
    return (
      <Panel title="Updates" headingLevel={3} padded>
        <p className="mm-quiet-note">Checking for updates...</p>
      </Panel>
    );
  }
  if (!status) {
    return (
      <Panel title="Updates" headingLevel={3} padded>
        <p className="mm-status-text text-sm" data-status="broken" role="alert">
          {errorMessage(
            updateStatusQ.error,
            "Could not check for updates right now.",
          )}
        </p>
      </Panel>
    );
  }
  return (
    <ReleaseStatus
      status={status}
      checking={updateStatusQ.isFetching}
      isWindows={isWindows}
      dockerCommand={
        status.install_type === "docker" && updateAvailable
          ? (status.docker_update_command ?? null)
          : null
      }
      onCheck={() => void updateStatusQ.refetch()}
    />
  );
}
