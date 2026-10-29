/**
 * The checks the Health card lists, one per thing Weir can look at: each switched-on workflow's folder chain, each
 * connection, each tool, each drive Weir writes to, the backups and Weir itself. Each says whether it passes, why in
 * a sentence, when it was last looked at and what to do about it. The card only draws what these return.
 */
import {
  answerWords,
  type ConnectionEntry,
} from "../../../../lib/connections/connection-model";
import { formatBytes } from "../../../../lib/format/bytes";
import type { SystemDrive } from "../../../../lib/system/system-stats-types";
import { HOME_NETWORK_WORDS } from "../../../../lib/settings/home-network-sign-in";
import { plural } from "../../../../lib/ui/mm-plural";
import type { StatusMeaning } from "../../../../lib/ui/status-meaning";
import type { ToolRow } from "../health-model";
import type { WorkflowHealth } from "../use-health";
import { problemWords } from "./health-words";
import { fullInWords } from "./system-words";
import {
  ABOUT_PATH,
  BACKUPS_PATH,
  MANAGERS_PATH,
  SECURITY_PATH,
  workflowPath,
} from "./system-paths";

export type HealthArea =
  "workflows" | "connections" | "tools" | "storage" | "backups" | "weir";

export const HEALTH_AREAS: readonly { key: HealthArea; name: string }[] = [
  { key: "workflows", name: "Workflows" },
  { key: "connections", name: "Connections" },
  { key: "tools", name: "Tools" },
  { key: "storage", name: "Storage" },
  { key: "backups", name: "Backups" },
  { key: "weir", name: "Weir" },
];

export type CheckFix = {
  label: string;
  to: string;
  /** What pressing it does, on hover. */
  note: string;
};

export type HealthCheck = {
  id: string;
  area: HealthArea;
  /** Attention and broken need you; done passes; idle has not been proven either way (a connection never tested). */
  meaning: StatusMeaning;
  /** A fact worth reading that is not a check, so no count includes it. */
  fact?: true;
  title: string;
  /** Why, in a sentence or two: the row's tooltip, and for a workflow what its Details say too. */
  why: string;
  /** Why, in a few words, as the row says it on one line. */
  words: string;
  /** When it was last looked at, in ms since the epoch. Null before the first look, and for a check that is live. */
  checkedAt: number | null;
  /** Its subject follows the stream second by second, so the row says it is live instead of when it was looked at. */
  live?: true;
  fix: CheckFix | null;
  /** What "Check again" looks at again: the check's area, and which one in it for a workflow or a connection. */
  again: { area: HealthArea; key: string | null };
  /** The workflow this check is about, whose folder chain "Details" opens. */
  workflowId: number | null;
};

const FIX_LABEL = "Fix it →";
/** A drive that fills in this many days is worth a look before it does. */
const FILLS_SOON_DAYS = 7;
/** A backup is overdue when it is older than this many times the interval between backups. */
const OVERDUE_INTERVALS = 2;
const MS_PER_HOUR = 3_600_000;

const link = (to: string, note: string): CheckFix => ({
  label: FIX_LABEL,
  to,
  note,
});

export function workflowChecks(
  workflows: readonly WorkflowHealth[],
): HealthCheck[] {
  return workflows.map((item) => {
    const { workflow, verdict, why } = item;
    const inSync = verdict.meaning === "done";
    return {
      id: `workflow:${workflow.id}`,
      area: "workflows",
      meaning: verdict.meaning,
      title:
        verdict.readiness === "needs_attention"
          ? `${workflow.name} needs a fix`
          : workflow.name,
      why: inSync
        ? "Its folders and connections are in sync."
        : (why ?? verdict.words),
      words: inSync
        ? "Folders and connections in sync"
        : problemWords(why ?? verdict.words),
      checkedAt: item.checkedAt,
      fix: inSync
        ? null
        : link(workflowPath(workflow.id), "Opens this workflow's settings."),
      again: { area: "workflows", key: String(workflow.id) },
      workflowId: workflow.id,
    };
  });
}

function connectionCheck(entry: ConnectionEntry): HealthCheck {
  const base = {
    id: `connection:${entry.key}`,
    area: "connections" as const,
    checkedAt: entry.checkedAt,
    again: { area: "connections" as const, key: entry.key },
    workflowId: null,
  };
  const fix = link(
    MANAGERS_PATH,
    "Opens the media managers and download clients.",
  );
  const took =
    entry.answerMs === null ? "" : ` in ${answerWords(entry.answerMs)}`;
  const down =
    entry.detail ||
    "Weir could not reach it. Check its address and that it is running.";
  switch (entry.state) {
    case "down":
      return {
        ...base,
        meaning: "broken",
        title: `${entry.name} isn't answering`,
        why: down,
        words: problemWords(down),
        fix,
      };
    case "slow":
      return {
        ...base,
        meaning: "attention",
        title: `${entry.name} is slow to answer`,
        why: `Its last answer came${took}.`,
        words: `Slow to answer${took}`,
        fix,
      };
    case "untested":
      return {
        ...base,
        meaning: "idle",
        title: entry.name,
        why: "Weir has not tested it yet.",
        words: "Not tested yet",
        fix: null,
      };
    default:
      return {
        ...base,
        meaning: "done",
        title: entry.name,
        why: `Answering${took}.`,
        words: `Answering${took}`,
        fix: null,
      };
  }
}

/** Each switched-on media manager and download client. */
export function connectionChecks(
  entries: readonly ConnectionEntry[],
): HealthCheck[] {
  return entries.filter((entry) => entry.enabled).map(connectionCheck);
}

export function toolChecks(
  tools: readonly ToolRow[] | null,
  checkedAt: number | null,
): HealthCheck[] {
  return (tools ?? []).map((tool): HealthCheck => {
    const base = {
      id: `tool:${tool.key}`,
      area: "tools" as const,
      checkedAt,
      again: { area: "tools" as const, key: null },
      workflowId: null,
    };
    if (tool.meaning === "broken") {
      return {
        ...base,
        meaning: "broken",
        title: `${tool.name} is missing`,
        why: "Weir cannot process files without it.",
        words: "Needed to process files",
        fix: link(ABOUT_PATH, "Opens System, where the tools are listed."),
      };
    }
    if (tool.meaning === "idle") {
      return {
        ...base,
        meaning: "idle",
        fact: true,
        title: `${tool.name} is not installed`,
        why: "It is optional: Weir writes with FFmpeg where it is missing.",
        words: "Optional · FFmpeg writes instead",
        fix: null,
      };
    }
    return {
      ...base,
      meaning: "done",
      title: tool.name,
      why: tool.banner,
      words: `Version ${tool.version}`,
      fix: null,
    };
  });
}

function driveCheck(drive: SystemDrive): HealthCheck {
  const free = formatBytes(drive.free_bytes);
  const first = drive.workflows[0];
  const fix = first
    ? link(
        workflowPath(first.id),
        "Opens the settings of a workflow that writes here.",
      )
    : null;
  const base = {
    id: `drive:${drive.path}`,
    area: "storage" as const,
    checkedAt: null,
    live: true as const,
    again: { area: "storage" as const, key: null },
    workflowId: null,
  };
  if (drive.keep_free_bytes > 0 && drive.free_bytes < drive.keep_free_bytes) {
    return {
      ...base,
      meaning: "attention",
      title: `${drive.name} is low on space`,
      why: `${free} free. Weir keeps ${formatBytes(drive.keep_free_bytes)} free here, so it holds new files until there is room.`,
      words: `${free} free · keeps ${formatBytes(drive.keep_free_bytes)}`,
      fix,
    };
  }
  if (drive.full_in_days !== null && drive.full_in_days <= FILLS_SOON_DAYS) {
    return {
      ...base,
      meaning: "attention",
      title: `${drive.name} fills up soon`,
      why: `${free} free. At this pace it is full in about ${plural(Math.max(1, Math.round(drive.full_in_days)), "day", "days")}.`,
      words: `${free} free · full in ${fullInWords(drive.full_in_days)}`,
      fix,
    };
  }
  return {
    ...base,
    meaning: "done",
    title: drive.name,
    why: `${free} free.`,
    words: `${free} free`,
    fix: null,
  };
}

/** Each drive any workflow reads from or writes to. A drive follows the stream second by second, so its row says it is live. */
export function storageChecks(
  drives: readonly SystemDrive[] | null,
): HealthCheck[] {
  return (drives ?? []).map(driveCheck);
}

export type BackupFacts = {
  enabled: boolean;
  intervalHours: number;
  /** The newest backup, made by hand or by itself, in ms since the epoch. */
  lastBackupAt: number | null;
  checkedAt: number | null;
};

/** The configuration backup: switched on, and recent enough for the interval it is set to. */
export function backupChecks(
  backups: BackupFacts | null,
  now: number,
): HealthCheck[] {
  if (backups === null) return [];
  const base = {
    id: "backups:configuration",
    area: "backups" as const,
    title: "Configuration backup",
    checkedAt: backups.checkedAt,
    again: { area: "backups" as const, key: null },
    workflowId: null,
  };
  const fix = link(BACKUPS_PATH, "Opens Backups.");
  if (!backups.enabled) {
    return [
      {
        ...base,
        meaning: "attention",
        why: "Automatic backups are off, so your settings are saved only when you back up by hand.",
        words: "Automatic backups off",
        fix,
      },
    ];
  }
  const overdueMs = backups.intervalHours * OVERDUE_INTERVALS * MS_PER_HOUR;
  if (backups.lastBackupAt === null || now - backups.lastBackupAt > overdueMs) {
    return [
      {
        ...base,
        meaning: "attention",
        why:
          backups.lastBackupAt === null
            ? "No backup has been made yet."
            : "The last backup is older than the schedule allows.",
        words:
          backups.lastBackupAt === null
            ? "No backup yet"
            : "Last backup is overdue",
        fix,
      },
    ];
  }
  return [
    {
      ...base,
      meaning: "done",
      why: "Backing up on schedule.",
      words: "On schedule",
      fix: null,
    },
  ];
}

export type WeirFacts = {
  /** What each worker that has stopped says, one string per worker. */
  stoppedWorkers: readonly string[];
  /** The newer version waiting, when there is one. */
  updateVersion: string | null;
  /** Whether devices on the home network skip sign-in, and the warning that comes with it; null until the server has said. */
  homeNetwork: { enabled: boolean; warning: string | null } | null;
  /** When the server last checked whether Weir is ready and its workers are running. */
  checkedAt: number | null;
};

function homeNetworkChecks(
  homeNetwork: WeirFacts["homeNetwork"],
  base: Pick<HealthCheck, "area" | "checkedAt" | "again" | "workflowId">,
): HealthCheck[] {
  if (homeNetwork === null) return [];
  const shared = {
    ...base,
    id: "weir:home-network-sign-in",
    title: HOME_NETWORK_WORDS.checkName,
  };
  if (homeNetwork.enabled && homeNetwork.warning !== null) {
    return [
      {
        ...shared,
        meaning: "attention",
        why: homeNetwork.warning,
        words: problemWords(homeNetwork.warning),
        fix: link(SECURITY_PATH, HOME_NETWORK_WORDS.checkFix),
      },
    ];
  }
  const words = homeNetwork.enabled
    ? HOME_NETWORK_WORDS.checkOn
    : HOME_NETWORK_WORDS.checkOff;
  return [{ ...shared, meaning: "done", why: words, words, fix: null }];
}

/** Weir's own background workers, whether devices on the home network skip sign-in, and a newer version when one is out. */
export function weirChecks(weir: WeirFacts | null): HealthCheck[] {
  if (weir === null) return [];
  const base = {
    area: "weir" as const,
    checkedAt: weir.checkedAt,
    again: { area: "weir" as const, key: null },
    workflowId: null,
  };
  const workers: HealthCheck =
    weir.stoppedWorkers.length > 0
      ? {
          ...base,
          id: "weir:workers",
          meaning: "broken",
          title: "A background worker has stopped",
          why: weir.stoppedWorkers[0],
          words: problemWords(weir.stoppedWorkers[0]),
          fix: link(ABOUT_PATH, "Opens System."),
        }
      : {
          ...base,
          id: "weir:workers",
          meaning: "done",
          title: "Background workers",
          why: "Every worker is running.",
          words: "All running",
          fix: null,
        };
  const checks = [workers, ...homeNetworkChecks(weir.homeNetwork, base)];
  if (weir.updateVersion === null) return checks;
  return [
    ...checks,
    {
      ...base,
      id: "weir:update",
      meaning: "todo",
      fact: true,
      title: `Weir ${weir.updateVersion} is out`,
      why: "A newer version is ready to install.",
      words: "Ready to install",
      fix: link(ABOUT_PATH, "Opens System, where updates are installed."),
    },
  ];
}
