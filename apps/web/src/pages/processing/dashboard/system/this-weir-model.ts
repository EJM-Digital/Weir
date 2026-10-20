/** What the This Weir card says: the health ring's figures and the fact tiles beside it, from how Weir itself is running. */
import { updateMeaning } from "../../../../lib/settings/update-status";
import { plural } from "../../../../lib/ui/mm-plural";
import type { StatusMeaning } from "../../../../lib/ui/status-meaning";
import type {
  RunsAs,
  SystemOverview,
} from "../../../../lib/system/system-stats-types";
import { parseAppTime } from "../../../../lib/ui/mm-format-date";
import { ago } from "../../processing-words";
import { sizeWords, splitAddress, uptimeWords } from "./system-words";

export type Fact = {
  key: string;
  label: string;
  value: string;
  /** What the value says where it has to be shorter to fit, when it can be. */
  valueShort?: string;
  sub: string;
  /** What the sub line means, when it says anything about how Weir is doing. */
  meaning?: StatusMeaning;
};

export type RingFigures = {
  /** How much of the ring is filled, 0 to 1. */
  fraction: number;
  passing: number;
  total: number;
  /** Checks that do not pass: what needs a person. */
  needYou: number;
  /** The ring's meaning: the worst check, or done when none needs a person. */
  meaning: StatusMeaning;
};

/** The checks the ring counts: those that pass, all of them, and those that need a person (a few are neither). */
export type RingChecks = {
  passing: number;
  total: number;
  need: number;
  meaning: StatusMeaning;
};

/** The ring's figures from the checks: no checks at all is a full ring, since nothing is wrong. */
export function ringFigures({
  passing,
  total,
  need,
  meaning,
}: RingChecks): RingFigures {
  const count = Math.max(0, total);
  const pass = Math.min(Math.max(0, passing), count);
  return {
    fraction: count === 0 ? 1 : pass / count,
    passing: pass,
    total: count,
    needYou: Math.max(0, need),
    meaning,
  };
}

const RUNS_AS_WORDS: Record<RunsAs, { value: string; sub: string }> = {
  service: { value: "Service", sub: "background" },
  app: { value: "App", sub: "signed in" },
  docker: { value: "Docker", sub: "container" },
};

const UPDATE_WORDS: Record<SystemOverview["update"]["status"], string> = {
  checking: "checking",
  up_to_date: "up to date",
  update_available: "update ready",
  downloaded: "update ready",
  not_published: "no release yet",
  rate_limited: "limited by GitHub",
  off: "checking is off",
  unavailable: "can't check",
};

function updateFact(
  update: SystemOverview["update"],
): Pick<Fact, "sub" | "meaning"> {
  const ready =
    update.status === "update_available" || update.status === "downloaded";
  if (ready && update.latest_version) {
    return {
      sub: `${update.latest_version} ready`,
      meaning: updateMeaning(update.status),
    };
  }
  return {
    sub: UPDATE_WORDS[update.status],
    meaning: updateMeaning(update.status),
  };
}

/** How quickly the server answers: the median to the millisecond, and what nearly all requests beat (p95). */
function responseFact(
  requests: SystemOverview["requests"],
): Pick<Fact, "value" | "sub"> {
  return {
    value: `${Math.round(requests.median_ms)} ms`,
    sub: `p95 ${Math.round(requests.p95_ms)} ms`,
  };
}

function backupSub(at: string | null, bytes: number | null): string {
  if (at === null) return "none made";
  return bytes === null ? "" : sizeWords(bytes);
}

/** The first part of a computer's name, before any dot: "media-pc" of "media-pc.home.lan"; an address of numbers stays whole. */
function shortHost(host: string): string | undefined {
  if (/^[\d.]+$/.test(host) || host.startsWith("[")) return undefined;
  const first = host.split(".")[0];
  return first && first !== host ? first : undefined;
}

export type FactsInput = {
  overview: SystemOverview;
  /** Passes running and the slots Weir has for them, from the newest reading; null before it arrives. */
  work: { running: number; slots: number } | null;
  /** Weir's own share of the computer from the newest reading; either part is null where it could not be read. */
  usage: { cpuPercent: number | null; memoryBytes: number | null } | null;
  /** Files waiting for a free slot, or null before Weir has said. */
  waiting: number | null;
  /** When the newest configuration backup was made, or null when there is none. */
  lastBackupAt: string | null;
  lastBackupBytes: number | null;
  now: number;
};

/**
 * The fact tiles, most useful first, so the whole rows a short card has room for are the ones that matter: what Weir is
 * and how long it has been up, what it is doing, whether it is safe, then how it answers and where it lives. Every one
 * is a reading; none is made up.
 */
export function weirFacts({
  overview,
  work,
  usage,
  waiting,
  lastBackupAt,
  lastBackupBytes,
  now,
}: FactsInput): Fact[] {
  const { host, port } = splitAddress(overview.address);
  const runsAs = RUNS_AS_WORDS[overview.runs_as];
  const failed = overview.jobs_today.failed;
  const startedAt = Date.parse(overview.started_at);
  const uptimeSeconds = Number.isNaN(startedAt)
    ? overview.uptime_seconds
    : Math.max(0, (now - startedAt) / 1000);
  return [
    {
      key: "version",
      label: "Version",
      value: overview.version,
      ...updateFact(overview.update),
    },
    {
      key: "uptime",
      label: "Uptime",
      value: uptimeWords(uptimeSeconds),
      sub:
        overview.restarts_this_week > 0
          ? plural(overview.restarts_this_week, "restart", "restarts")
          : "no restarts",
      meaning: overview.restarts_this_week > 0 ? "attention" : "done",
    },
    {
      key: "files-at-once",
      label: "Files at once",
      value: work ? `${work.running} / ${work.slots}` : "–",
      sub: work && work.running > 0 ? "running" : "idle",
    },
    {
      key: "jobs",
      label: "Jobs today",
      value: `${overview.jobs_today.run.toLocaleString()} run`,
      valueShort: overview.jobs_today.run.toLocaleString(),
      sub: failed > 0 ? `${failed.toLocaleString()} failed` : "none failed",
      meaning: failed > 0 ? "broken" : "done",
    },
    {
      key: "usage",
      label: "Usage",
      value:
        usage?.cpuPercent == null
          ? "–"
          : `${Math.round(usage.cpuPercent)}% CPU`,
      sub:
        usage?.memoryBytes == null ? "" : `${sizeWords(usage.memoryBytes)} RAM`,
    },
    {
      key: "waiting",
      label: "Waiting",
      value: waiting === null ? "–" : waiting.toLocaleString(),
      sub:
        waiting === null
          ? ""
          : waiting > 0
            ? "for a free slot"
            : "nothing queued",
    },
    {
      key: "backup",
      label: "Last backup",
      value: lastBackupAt ? ago(lastBackupAt, now) : "None yet",
      sub: backupSub(lastBackupAt, lastBackupBytes),
    },
    {
      key: "response",
      label: "Response",
      ...responseFact(overview.requests),
    },
    {
      key: "address",
      label: "Address",
      value: host || "–",
      valueShort: shortHost(host),
      sub: port ? `port ${port}` : "",
    },
    { key: "runs-as", label: "Runs as", ...runsAs },
    {
      key: "data",
      label: "Data",
      value: sizeWords(overview.data_bytes),
      sub: "on disk",
    },
    {
      key: "browsers",
      label: "Browsers",
      value: overview.browsers_live.toLocaleString(),
      sub: "open now",
    },
  ];
}

export type BackupEntry = { created_at: string; size_bytes: number };

/** The most recent backup of a list that may be in any order; null when there are none. */
export function newestBackup<T extends BackupEntry>(
  items: readonly T[],
): T | null {
  return items.reduce<T | null>(
    (latest, item) =>
      latest === null ||
      (parseAppTime(item.created_at) ?? 0) >
        (parseAppTime(latest.created_at) ?? 0)
        ? item
        : latest,
    null,
  );
}
