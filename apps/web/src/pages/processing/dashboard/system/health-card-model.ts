/** What the Health card says: the checks counted by area, the headline, and which checks the ribbon's filter shows. */
import {
  needsYou,
  type StatusMeaning,
} from "../../../../lib/ui/status-meaning";
import {
  HEALTH_AREAS,
  type HealthArea,
  type HealthCheck,
} from "./health-checks";

export type AreaTally = {
  key: HealthArea;
  name: string;
  /** Checks that pass. */
  ok: number;
  /** Checks in the area, not counting notes. */
  total: number;
  /** The worst of the area's checks: broken, then attention, else done. */
  meaning: StatusMeaning;
};

export type HealthSummary = {
  pass: number;
  total: number;
  /** Checks that need you: a broken one, or one that needs attention. */
  need: number;
  /** The worst of the checks that need you, or done when none does. */
  meaning: StatusMeaning;
};

/** Facts are not checks: they never count towards a total. */
const isCounted = (check: HealthCheck): boolean => !check.fact;

/** One tally for each area, in the ribbon's order. A broken check makes the area broken, one that needs attention makes it so. */
export function areaTallies(checks: readonly HealthCheck[]): AreaTally[] {
  return HEALTH_AREAS.map(({ key, name }) => {
    const own = checks.filter((check) => check.area === key);
    const counted = own.filter(isCounted);
    return {
      key,
      name,
      ok: counted.filter((check) => check.meaning === "done").length,
      total: counted.length,
      meaning: own.some((check) => check.meaning === "broken")
        ? "broken"
        : own.some((check) => check.meaning === "attention")
          ? "attention"
          : "done",
    };
  });
}

export function healthSummary(checks: readonly HealthCheck[]): HealthSummary {
  const counted = checks.filter(isCounted);
  const needing = counted.filter((check) => needsYou(check.meaning));
  return {
    pass: counted.filter((check) => check.meaning === "done").length,
    total: counted.length,
    need: needing.length,
    meaning: needing.some((check) => check.meaning === "broken")
      ? "broken"
      : needing.length > 0
        ? "attention"
        : "done",
  };
}

/**
 * The words beside the card's title: "9 of 10 pass · 1 needs you". While a folder check has not answered, Weir's
 * own count of its checks (`overview`) stands in, so the figure is there before every row is.
 */
export function healthHeadline(
  summary: HealthSummary,
  checking: boolean,
  overview: { passing: number; total: number } | null,
): string {
  const { pass, total } =
    checking && overview
      ? { pass: overview.passing, total: overview.total }
      : summary;
  if (total === 0) return "";
  const need =
    summary.need === 0
      ? "all good"
      : `${summary.need.toLocaleString()} ${summary.need === 1 ? "needs" : "need"} you`;
  return `${pass.toLocaleString()} of ${total.toLocaleString()} pass · ${need}`;
}

/** The worst first: broken, attention, then what has not been proven, what is being worked on or waits, and what passes. */
const WORST_FIRST: Record<StatusMeaning, number> = {
  broken: 0,
  attention: 1,
  idle: 2,
  doing: 3,
  todo: 4,
  done: 5,
};

/**
 * The checks the card lists: every check, or every check of the area picked, with the problems first and the worst of
 * them first, so what needs you is never below a row that is fine.
 */
export function listedChecks(
  checks: readonly HealthCheck[],
  area: HealthArea | null,
): HealthCheck[] {
  const wanted =
    area === null ? checks : checks.filter((check) => check.area === area);
  return [...wanted].sort(
    (a, b) =>
      Number(Boolean(a.fact)) - Number(Boolean(b.fact)) ||
      WORST_FIRST[a.meaning] - WORST_FIRST[b.meaning],
  );
}

/** What the list says when it has no row to show. */
export function emptyHealthWords(area: HealthArea | null): string {
  if (area === null) return "Weir is still looking.";
  const { name } = HEALTH_AREAS.find((entry) => entry.key === area) as {
    name: string;
  };
  return `Nothing to show for ${name}.`;
}
