/**
 * The Dashboard's System view: how Weir itself is running and what it is doing in the background, as live cards on
 * Live's grid. The band across the top has This Weir (a ring of the checks that pass, and tiles of facts), This
 * computer (CPU, memory and disk) and Processing (Weir's own disk work and speed), with Storage beside it. Health,
 * Connections, Scheduled tasks, the Log, and Backups and tools are the rows below. Every figure follows the
 * Activity stream's once-a-second readings; nothing polls fast. Each card loads and fails on its own. The jobs Weir has
 * run are in System › Logs, one link from Scheduled tasks.
 */
import { useState } from "react";

import { ApiEntryError } from "../../../components/shared/api-entry-error";
import { PageLoading } from "../../../components/shared/page-loading";
import type { ProcessingLibrary } from "../../../lib/processing/libraries-api";
import { useProcessingLibrariesQuery } from "../../../lib/processing/libraries-queries";
import {
  useSystemOverviewQuery,
  useSystemStatsFrames,
} from "../../../lib/system/use-system-stats";
import { classNames } from "../../../lib/ui/class-names";
import { LOW_COLUMNS, type PageLayout } from "./dashboard-layout";
import { BackupsCard } from "./system/backups-card";
import { ConnectionsSlot } from "./system/connections-slot";
import { healthSummary } from "./system/health-card-model";
import { HealthCard } from "./system/health-card";
import { LogCard } from "./system/log-card";
import { ProcessingCard } from "./system/processing-card";
import { StorageCard } from "./system/storage-card";
import { SystemGrid } from "./system/system-grid";
import { TasksCard } from "./system/tasks-card";
import { ThisComputerCard } from "./system/this-computer-card";
import { ThisWeirCard } from "./system/this-weir-card";
import { useHealthChecks } from "./system/use-health-checks";

const NO_WORKFLOWS: readonly ProcessingLibrary[] = [];

type SystemViewProps = {
  /** How the page lays itself out, decided from the width of its main area. */
  layout: PageLayout;
};

export function SystemView({ layout }: SystemViewProps) {
  useSystemStatsFrames();
  const workflows = useProcessingLibrariesQuery();
  const overview = useSystemOverviewQuery();
  const health = useHealthChecks(workflows.data ?? NO_WORKFLOWS);
  const [jump, setJump] = useState(0);

  if (workflows.isPending) return <PageLoading label="Loading the system" />;
  if (workflows.isError && !workflows.data)
    return <ApiEntryError error={workflows.error} />;

  const summary = healthSummary(health.checks);
  const ringChecks =
    health.checking && overview.data
      ? {
          ...overview.data.checks,
          need: summary.need,
          meaning: summary.meaning,
        }
      : {
          passing: summary.pass,
          total: summary.total,
          need: summary.need,
          meaning: summary.meaning,
        };
  const across = layout.band === "across";
  return (
    <SystemGrid layout={layout}>
      <div
        className={classNames(
          "mm-sy-cell mm-sy-cell--band",
          across ? "mm-sy-cell--across" : "mm-sy-cell--stacked",
        )}
      >
        <ThisWeirCard
          checks={ringChecks}
          onShowHealth={() => setJump((count) => count + 1)}
        />
        <ThisComputerCard />
        <ProcessingCard />
      </div>
      <div className="mm-sy-cell mm-sy-cell--store">
        <StorageCard />
      </div>
      <div className="mm-sy-cell mm-sy-cell--mid">
        <HealthCard workflows={workflows.data} jump={jump} />
      </div>
      <div className="mm-sy-cell mm-sy-cell--conn">
        <ConnectionsSlot workflows={workflows.data} />
      </div>
      <div
        className="mm-sy-cell mm-sy-cell--low"
        style={
          layout.sideBySide ? { gridTemplateColumns: LOW_COLUMNS } : undefined
        }
      >
        <TasksCard />
        <LogCard />
      </div>
      <div className="mm-sy-cell mm-sy-cell--upd">
        <BackupsCard />
      </div>
    </SystemGrid>
  );
}
