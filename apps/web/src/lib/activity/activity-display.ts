/**
 * How one Activity entry reads in the log: a title, a one-line summary, what it means and, when it says more
 * than the title, a badge. Pure, so the log and anything else that lists entries agree.
 */
import type { ActivityEventItem } from "../api/types";
import { baseName } from "../format/path";
import { asBoolean, asNumber, asString, parseActivityDetail } from "./detail";
import { EVENT_LABELS } from "./event-labels";
import {
  FILE_PROGRESS_EVENT,
  REMUX_PASS_COMPLETED_EVENT,
  SKIPPED_REPEAT_EVENT,
} from "./event-types";
import { finishedNothing, isRejectedByRules } from "./pass-detail";
import type { StatusMeaning } from "../ui/status-meaning";

export type ActivityDisplay = {
  title: string;
  summary: string;
  detail: string | null;
  /** A status badge, only when it says more than the title does. */
  chip: string | null;
  /** What it means: done, under way, needs a look, broken, or only information (idle). */
  meaning: StatusMeaning;
  compact: boolean;
};

/** Routine processing events: one fixed title, a compact row. */
const ROUTINE_PROCESSING: Record<string, { title: string; summary: string }> = {
  "processing.supplied_payload_evaluation_completed": {
    title: "Manual queue check finished",
    summary: "Download queue safety check",
  },
  "processing.candidate_gate_completed": {
    title: "Queue check finished",
    summary: "Download queue safety check",
  },
  "processing.work_temp_stale_sweep_completed": {
    title: "Temporary files cleanup finished",
    summary: "Background cleanup result",
  },
  "processing.failure_cleanup_sweep_completed": {
    title: "Cleanup after failed processing finished",
    summary: "Background cleanup result",
  },
};

/** An event type as a person reads it; an unknown one falls back to its last part, in words. */
export function eventLabel(eventType: string): string {
  return (
    EVENT_LABELS[eventType] ??
    eventType.split(".").slice(-1)[0].replaceAll("_", " ")
  );
}

function progressDisplay(ev: ActivityEventItem): ActivityDisplay {
  const parsed = parseActivityDetail(ev.detail);
  const status = asString(parsed?.status);
  const percent = asNumber(parsed?.percent);
  const name = baseName(asString(parsed?.relative_media_path) ?? "") || "file";
  return {
    title:
      status === "finished"
        ? `${name} finished processing`
        : status === "failed"
          ? `${name} could not be processed`
          : `Processing ${name}`,
    summary:
      percent == null
        ? "Preparing the cleaned-up file"
        : `Writing the cleaned-up file (${Math.round(percent)}%)`,
    detail: ev.detail ?? null,
    chip:
      status === "failed"
        ? "Processing stopped"
        : status === "finished"
          ? "Processing finished"
          : "Processing now",
    meaning:
      status === "failed" ? "broken" : status === "finished" ? "done" : "doing",
    compact: false,
  };
}

function passDisplay(ev: ActivityEventItem): ActivityDisplay {
  const parsed = parseActivityDetail(ev.detail);
  const outcome = asString(parsed?.outcome);
  if (finishedNothing(outcome)) {
    return {
      title: ev.title,
      summary: asString(parsed?.reason) ?? "Weir did not process this file",
      detail: ev.detail ?? null,
      chip:
        asString(parsed?.skip_kind) === "below_minimum_size"
          ? "Under the minimum size"
          : outcome === "source_not_ready"
            ? "Waiting"
            : "Skipped",
      meaning: "idle",
      compact: false,
    };
  }
  const remuxNeeded = asBoolean(parsed?.remux_required);
  const passedThrough = asBoolean(parsed?.pass_through_unchanged) === true;
  const noChanges = outcome === "live_skipped_not_required";
  const failed = outcome?.startsWith("failed") ?? false;
  const rejected = isRejectedByRules(parsed);
  const fileName =
    baseName(asString(parsed?.relative_media_path) ?? "") ||
    baseName(asString(parsed?.inspected_source_path) ?? "") ||
    "File";
  return {
    title: passedThrough
      ? `${fileName} was passed through unchanged`
      : rejected
        ? `Rejected ${fileName}`
        : noChanges
          ? `No changes needed for ${fileName}`
          : failed
            ? `${fileName} could not be processed`
            : `${fileName} was processed successfully`,
    summary: passedThrough
      ? "Handed back without applying your rules"
      : rejected
        ? (asString(parsed?.reason) ?? "Weir rejected this file")
        : noChanges
          ? "No changes were needed"
          : failed
            ? "Weir could not finish this file"
            : remuxNeeded === false
              ? "The file already fits your rules"
              : "Cleaned-up file written",
    detail: ev.detail ?? null,
    chip: passedThrough
      ? "Passed through"
      : rejected
        ? "Rejected"
        : noChanges
          ? "No changes needed"
          : failed
            ? "Processing failed"
            : "File processed",
    meaning:
      rejected || passedThrough
        ? "attention"
        : failed || ev.detail?.includes('"ok":false')
          ? "broken"
          : "done",
    compact: false,
  };
}

function authDisplay(ev: ActivityEventItem): ActivityDisplay {
  return {
    title: eventLabel(ev.event_type),
    summary: ev.module.startsWith("arr_library")
      ? "Service connection check"
      : "Account and sign-in activity",
    detail: ev.detail ?? null,
    chip: null,
    meaning:
      ev.event_type.includes("failed") || ev.event_type.includes("denied")
        ? "attention"
        : ev.event_type.includes("succeeded") ||
            ev.event_type.includes("changed")
          ? "done"
          : "idle",
    compact: false,
  };
}

/**
 * Left alone, and nothing for anyone to do: the server's own title names the file and whether it was imported. Listed
 * in full, whatever the length of its detail, when the person asked for the file again and is waiting to see this
 * line; a manager resending hand-offs is routine and is counted, so it cannot push real news off the stream.
 */
function skippedRepeatDisplay(ev: ActivityEventItem): ActivityDisplay {
  const trigger = asString(parseActivityDetail(ev.detail)?.trigger);
  return {
    title: ev.title || eventLabel(ev.event_type),
    summary: "Processing",
    detail: ev.detail ?? null,
    chip: null,
    meaning: "idle",
    compact: trigger !== "manual",
  };
}

/** How one entry reads in the log. */
export function eventDisplay(ev: ActivityEventItem): ActivityDisplay {
  if (ev.event_type === FILE_PROGRESS_EVENT) return progressDisplay(ev);
  if (ev.event_type === REMUX_PASS_COMPLETED_EVENT) return passDisplay(ev);
  if (ev.event_type === SKIPPED_REPEAT_EVENT) return skippedRepeatDisplay(ev);
  const routine = ROUTINE_PROCESSING[ev.event_type];
  if (routine) {
    return {
      ...routine,
      detail: ev.detail ?? null,
      chip: null,
      meaning: "done",
      compact: true,
    };
  }
  if (ev.module.startsWith("auth") || ev.module.startsWith("arr_library")) {
    return authDisplay(ev);
  }

  const lowered = `${ev.title} ${ev.detail ?? ""}`.toLowerCase();
  const meaning: StatusMeaning = /(error|failed|denied)/.test(lowered)
    ? "broken"
    : /(skip|missing|review|warning|not configured|unsupported)/.test(lowered)
      ? "attention"
      : /(completed|finished|saved|updated|started)/.test(lowered)
        ? "done"
        : "idle";
  return {
    title: eventLabel(ev.event_type),
    summary: ev.module === "processing" ? "Processing" : "System",
    detail: ev.detail ?? null,
    chip: null,
    meaning,
    compact: Boolean(ev.detail && ev.detail.length > 120),
  };
}

/** Short plain-text detail (a username, a one-line reason) reads best on the summary line. */
export function inlineDetailOf(
  ev: ActivityEventItem,
  display: ActivityDisplay,
): string | null {
  if (!display.detail || display.compact) return null;
  if (
    ev.event_type === FILE_PROGRESS_EVENT ||
    ev.event_type === REMUX_PASS_COMPLETED_EVENT
  ) {
    return null;
  }
  return parseActivityDetail(display.detail) ? null : display.detail;
}

/** The event types present in a list, labelled and sorted, for the Event filter. */
export function eventOptions(
  items: ActivityEventItem[],
): { value: string; label: string }[] {
  const seen = new Map<string, string>();
  for (const item of items) {
    if (!seen.has(item.event_type)) {
      seen.set(item.event_type, eventLabel(item.event_type));
    }
  }
  return Array.from(seen.entries())
    .map(([value, label]) => ({ value, label }))
    .sort((a, b) => a.label.localeCompare(b.label));
}
