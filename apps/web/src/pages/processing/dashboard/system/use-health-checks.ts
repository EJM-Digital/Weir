import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";

import { useConnections } from "../../../../lib/connections/use-connections";
import { useHomeNetworkSignInQuery } from "../../../../lib/settings/home-network-sign-in";
import type { ProcessingLibrary } from "../../../../lib/processing/libraries-api";
import {
  useAppSettingsQuery,
  useConfigurationBackupsQuery,
  useUpdateStatusQuery,
} from "../../../../lib/settings/queries";
import { systemKeys } from "../../../../lib/system/query-keys";
import { useMediaToolsQuery } from "../../../../lib/system/media-tools";
import { useServerLooks } from "../../../../lib/system/use-server-looks";
import { useSystemReadinessQuery } from "../../../../lib/system/readiness-queries";
import { fetchSystemStats } from "../../../../lib/system/system-stats-api";
import { useSystemOverviewQuery } from "../../../../lib/system/use-system-stats";
import { parseAppTime } from "../../../../lib/ui/mm-format-date";
import { useNow } from "../../../../lib/ui/use-now";
import { CHECKING_WORDS, newestTime } from "../health-model";
import { useConnectionTesting } from "../use-connection-testing";
import { useHealth, type Health } from "../use-health";
import {
  backupChecks,
  connectionChecks,
  storageChecks,
  toolChecks,
  weirChecks,
  workflowChecks,
  type BackupFacts,
  type HealthCheck,
  type WeirFacts,
} from "./health-checks";

/** Backups are judged against hours, so a minute is as often as the time they are measured against needs to move. */
const NOW_TICK_MS = 60_000;

export type HealthChecks = {
  checks: HealthCheck[];
  /** A workflow's folder check has not answered yet. */
  checking: boolean;
  /** Weir's own count of its checks, for the headline before every row has answered. */
  overviewChecks: { passing: number; total: number } | null;
  health: Health;
  /** When any part of the card was last read again, for the card's sheen. Null before the first read. */
  readAt: number | null;
  /** The ids of the checks being looked at again now. */
  busy: ReadonlySet<string>;
  /** Looks at one check's subject again. */
  again: (check: HealthCheck) => Promise<void>;
};

/**
 * Every check the Health card lists, read from the parts Weir already knows: each switched-on workflow's folder
 * chain, the connections, the tools, the drives, the backup schedule and Weir's workers. Each part loads and fails
 * on its own, so a check appears as soon as its part has answered.
 */
export function useHealthChecks(
  workflows: readonly ProcessingLibrary[],
): HealthChecks {
  const health = useHealth(workflows);
  const { entries } = useConnections(health.managers, health.downloadClients);
  const testing = useConnectionTesting();
  const tools = useMediaToolsQuery();
  const stats = useQuery({
    queryKey: systemKeys.stats,
    queryFn: fetchSystemStats,
    select: (value) => value.drives,
  });
  const settings = useAppSettingsQuery();
  const backupList = useConfigurationBackupsQuery(true);
  const readiness = useSystemReadinessQuery();
  const update = useUpdateStatusQuery();
  const homeNetwork = useHomeNetworkSignInQuery();
  const overview = useSystemOverviewQuery();
  const looks = useServerLooks();
  const now = useNow(NOW_TICK_MS);
  const [busy, setBusy] = useState<ReadonlySet<string>>(new Set());

  const backups = useMemo<BackupFacts | null>(() => {
    if (!settings.data) return null;
    const newest = Math.max(
      parseAppTime(settings.data.configuration_backup_last_run_at) ?? 0,
      ...(backupList.data?.items ?? []).map(
        (item) => parseAppTime(item.created_at) ?? 0,
      ),
    );
    return {
      enabled: settings.data.configuration_backup_enabled,
      intervalHours: settings.data.configuration_backup_interval_hours,
      lastBackupAt: newest > 0 ? newest : null,
      checkedAt: settings.dataUpdatedAt || null,
    };
  }, [settings.data, settings.dataUpdatedAt, backupList.data]);

  const weir = useMemo<WeirFacts | null>(() => {
    if (!readiness.data) return null;
    return {
      stoppedWorkers: (readiness.data.worker_health ?? [])
        .filter((worker) => worker.stopped_workers + worker.stale_workers > 0)
        .map((worker) => worker.detail),
      updateVersion:
        update.data?.status === "update_available"
          ? (update.data.latest_version ?? null)
          : null,
      homeNetwork: homeNetwork.data
        ? {
            enabled: homeNetwork.data.enabled,
            warning: homeNetwork.data.warning ?? null,
          }
        : null,
      checkedAt: newestTime(
        readiness.dataUpdatedAt || null,
        looks.readinessCheckedAt,
      ),
    };
  }, [
    readiness.data,
    readiness.dataUpdatedAt,
    looks.readinessCheckedAt,
    update.data,
    homeNetwork.data,
  ]);

  const checks = useMemo(
    () => [
      ...workflowChecks(health.workflows),
      ...connectionChecks(entries),
      ...toolChecks(
        health.tools,
        tools.dataUpdatedAt > 0 ? tools.dataUpdatedAt : null,
      ),
      ...storageChecks(stats.data ?? null),
      ...backupChecks(backups, now),
      ...weirChecks(weir),
    ],
    [
      health.workflows,
      entries,
      health.tools,
      tools.dataUpdatedAt,
      stats.data,
      backups,
      now,
      weir,
    ],
  );

  const readAt = newestTime(
    health.foldersReadAt,
    ...entries.map((entry) => entry.checkedAt),
    tools.dataUpdatedAt || null,
    settings.dataUpdatedAt || null,
    readiness.dataUpdatedAt || null,
  );

  const lookAgain = async (check: HealthCheck): Promise<void> => {
    const { area, key } = check.again;
    switch (area) {
      case "workflows":
        await health.workflows
          .find((item) => String(item.workflow.id) === key)
          ?.recheck();
        return;
      case "connections": {
        const entry = entries.find((candidate) => candidate.key === key);
        if (entry) await testing.test(entry);
        return;
      }
      case "tools":
        await tools.refetch();
        return;
      case "storage":
        await stats.refetch();
        return;
      case "backups":
        await Promise.all([settings.refetch(), backupList.refetch()]);
        return;
      case "weir":
        await Promise.all([
          readiness.refetch(),
          update.refetch(),
          homeNetwork.refetch(),
        ]);
        return;
    }
  };

  const again = async (check: HealthCheck): Promise<void> => {
    setBusy((current) => new Set(current).add(check.id));
    try {
      await lookAgain(check);
    } finally {
      setBusy((current) => {
        const next = new Set(current);
        next.delete(check.id);
        return next;
      });
    }
  };

  return {
    checks,
    checking: health.workflows.some(
      (item) => item.verdict.words === CHECKING_WORDS,
    ),
    overviewChecks: overview.data?.checks ?? null,
    health,
    readAt,
    busy,
    again,
  };
}
