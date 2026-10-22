import type {
  ProcessingFile,
  ProcessingFileHandback,
  ProcessingFileLogEntry,
} from "../../lib/processing/files-api";
import type { ProcessingLibrary } from "../../lib/processing/libraries-api";
import {
  workflowKindName,
  type WorkflowKind,
} from "../../lib/processing/workflow-kind";
import {
  audioCodecName,
  channelLayout,
  languageName,
} from "../../lib/format/track";
import { parseAppDate, parseAppTime } from "../../lib/ui/mm-format-date";
import type { StatusMeaning } from "../../lib/ui/status-meaning";

/** A server time as epoch ms. Its times carry no zone and are UTC. */
export function serverMs(iso: string): number {
  return parseAppDate(iso).getTime();
}

/** "just now", "6 min ago", "2 h ago", "3 days ago". */
export function agoWords(iso: string | null | undefined, now: number): string {
  const at = parseAppTime(iso);
  if (at === null) return "";
  const seconds = Math.max(0, Math.round((now - at) / 1000));
  if (seconds < 60) return "just now";
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 48) return `${hours} h ago`;
  return `${Math.round(hours / 24)} days ago`;
}

/** How long a pass took, in the words a person uses. */
export function tookWords(seconds: number | null | undefined): string | null {
  if (typeof seconds !== "number" || !Number.isFinite(seconds) || seconds < 0) {
    return null;
  }
  const whole = Math.round(seconds);
  if (whole < 60) return `${whole} s`;
  const minutes = Math.floor(whole / 60);
  if (minutes < 60) return `${minutes} min ${whole % 60} s`;
  return `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}

/** One track as Activity lists it: what it is, and what happened to it. */
export type ActivityTrack = {
  kind: "Audio" | "Subtitle" | "Other";
  what: string;
  kept: boolean;
  /** Why it went, in the planner's own words. Empty for a kept track. */
  why: string;
};

function textOf(value: unknown): string {
  return typeof value === "string" ? value.trim() : "";
}

function listOf(value: unknown): string[] {
  return Array.isArray(value)
    ? value.filter((v): v is string => typeof v === "string" && v.trim() !== "")
    : [];
}

/** The planner's "a · b · c" lines, one entry per track; the dash it writes for "none" is no track. */
function splitLine(line: unknown): string[] {
  return textOf(line)
    .split(" · ")
    .map((part) => part.trim())
    .filter(
      (part) => part !== "" && part !== "—" && part !== "-" && part !== "None",
    );
}

/**
 * The planner describes a track the way ffprobe does ("fre eac3 6 ch (stream 2)"). Put it the way the
 * rest of Activity reads ("French 5.1 E-AC-3"): language named, channels as a layout, codec spelled out,
 * stream numbers dropped.
 */
export function readableTrack(text: string): string {
  return text
    .replace(/\s*\(stream \d+\)/gi, "")
    .replace(/\b[a-z]{3}\b(?=\s+[a-z0-9_]+\s+\d+\s*ch\b)/gi, languageName)
    .replace(
      /\b([a-z0-9_]+)\s+(\d+)\s*ch\b/gi,
      (_, codec: string, ch: string) =>
        `${channelLayout(Number(ch))} ${audioCodecName(codec)}`,
    )
    .replace(/^[a-z]{3}$/, languageName)
    .trim();
}

/** The planner's reason, with any track it names made readable and the first letter raised. */
function readableWhy(text: string): string {
  const why = readableTrack(text.trim());
  return why ? why[0].toUpperCase() + why.slice(1) : "";
}

/**
 * A removal line reads "English 2.0 (commentary excluded — remove commentary enabled)" or
 * "fre eac3 6 ch (stream 2): removed (not selected — …)". Split it into the track and the reason.
 */
function splitRemoval(line: string): { what: string; why: string } {
  const clean = line.replace(/\s*\(stream \d+\)/gi, "");
  const colon = clean.match(/^(.*?):\s*removed\s*\((.*)\)\s*$/i);
  if (colon) {
    return { what: readableTrack(colon[1]), why: readableWhy(colon[2]) };
  }
  const paren = clean.match(/^(.*?)\s*\((.*)\)\s*$/);
  if (paren) {
    return { what: readableTrack(paren[1]), why: readableWhy(paren[2]) };
  }
  return { what: readableTrack(clean), why: "" };
}

/**
 * Every track the pass kept and removed, from the record it saved. Kept tracks come from the "after" lines,
 * removed ones from the removal lists with the planner's reason; extras (cover images, attachments) are
 * listed when the pass took them out.
 */
export function tracksFromRecord(
  detail: Record<string, unknown>,
): ActivityTrack[] {
  const tracks: ActivityTrack[] = [];
  for (const what of splitLine(detail.audio_after)) {
    tracks.push({ kind: "Audio", what, kept: true, why: "" });
  }
  for (const line of listOf(detail.removed_audio)) {
    tracks.push({ kind: "Audio", ...splitRemoval(line), kept: false });
  }
  for (const what of splitLine(detail.subs_after)) {
    tracks.push({ kind: "Subtitle", what, kept: true, why: "" });
  }
  for (const line of listOf(detail.removed_subtitles)) {
    tracks.push({ kind: "Subtitle", ...splitRemoval(line), kept: false });
  }
  for (const line of [
    ...listOf(detail.removed_images),
    ...listOf(detail.removed_attachments),
  ]) {
    tracks.push({ kind: "Other", ...splitRemoval(line), kept: false });
  }
  return tracks;
}

/** Before, after and saved, when the record has both sizes. */
export function sizesFromRecord(detail: Record<string, unknown>): {
  before: number;
  after: number;
  saved: number;
} | null {
  const before = detail.source_size_bytes;
  const after = detail.output_size_bytes;
  if (typeof before !== "number" || typeof after !== "number") return null;
  if (before <= 0 || after <= 0) return null;
  return { before, after, saved: Math.max(0, before - after) };
}

/** A file's sizes as Activity shows them: always all three, with a reason rather than a gap. */
export type DetailSizes = {
  before: number | null;
  /** Null when Weir wrote no new copy of the file, or did not record how big it is. */
  after: number | null;
  saved: number | null;
  /** Weir cleaned the file but its record holds no sizes (an older record). */
  notRecorded: boolean;
  /** Why After is what it is, when that is not obvious. */
  note: string | null;
};

/** Whether the record says Weir handed the file back without rewriting it: passed through, or already clean. */
function wentThroughUnchanged(detail: Record<string, unknown>): boolean {
  return (
    detail.pass_through_unchanged === true ||
    detail.output_copied_without_remux === true ||
    detail.outcome === "live_skipped_not_required"
  );
}

/**
 * Before, After and Saved for the open file, whatever happened to it. A pass that measured both sizes gives them. A file
 * Weir handed back without changing it ("already clean", passed through) is the same size after and saved nothing, and
 * only a record that says so is told that way. A file Weir cleaned whose record holds no sizes says "not recorded" rather
 * than showing figures it does not have. A file Weir has not written a copy of shows its size and says so.
 */
export function detailSizes(
  file: Pick<ProcessingFile, "status" | "size_bytes">,
  detail: Record<string, unknown> | null,
): DetailSizes {
  const measured = detail ? sizesFromRecord(detail) : null;
  const unchanged = detail ? wentThroughUnchanged(detail) : false;
  if (measured) {
    return {
      ...measured,
      notRecorded: false,
      note: unchanged
        ? "Weir handed the file back as it was, so its size did not change."
        : null,
    };
  }
  const recorded = detail?.source_size_bytes ?? detail?.source_fingerprint_size;
  const before =
    typeof recorded === "number" && recorded > 0
      ? recorded
      : file.size_bytes > 0
        ? file.size_bytes
        : null;
  if (file.status === "passed_through" || unchanged) {
    return {
      before,
      after: before,
      saved: before == null ? null : 0,
      notRecorded: false,
      note: "Weir handed the file back as it was, so its size did not change.",
    };
  }
  if (detail && file.status === "processed") {
    return {
      before,
      after: null,
      saved: null,
      notRecorded: true,
      note: "Size not recorded: Weir cleaned this file but did not keep its sizes, so how much it saved is not known.",
    };
  }
  return {
    before,
    after: null,
    saved: null,
    notRecorded: false,
    note: "Weir has not written a new copy of this file.",
  };
}

/** What became of the copy Weir handed back, as Activity tells it. */
export type HandbackStory = {
  heading: string;
  sentence: string;
  meaning: StatusMeaning;
};

/**
 * What became of the copy Weir handed back (#652): "Imported by Sonarr", "Deluno will not import it", or still
 * waiting for a media manager, with what Weir did with its copy. A Weir-only workflow has no manager to wait
 * for, so its copy is simply in the output folder. Null when Weir wrote no copy.
 */
export function handbackStory(
  handback: ProcessingFileHandback | null | undefined,
  now: number,
  workflowKind: WorkflowKind["kind"],
): HandbackStory | null {
  if (!handback) return null;
  const by = handback.outcome_by ?? "Your media manager";
  const note = handback.release_note ?? "";
  if (handback.outcome === "imported") {
    const when = handback.outcome_at
      ? ` ${agoWords(handback.outcome_at, now)}`
      : "";
    const where = handback.imported_path
      ? ` It is in the library at ${handback.imported_path}.`
      : "";
    return {
      heading: `Imported by ${handback.outcome_by ?? "your media manager"}`,
      sentence: `${by} imported it${when}.${where} ${note}`.trim(),
      meaning: "done",
    };
  }
  if (handback.outcome === "not-imported") {
    return {
      heading: `${by} will not import it`,
      sentence:
        note ||
        `${by} will not import this file. Weir kept its copy at ${handback.output_path}.`,
      meaning: "attention",
    };
  }
  if (handback.settled_at && note) {
    return { heading: "Handed back", sentence: note, meaning: "idle" };
  }
  const written = handback.written_at
    ? agoWords(handback.written_at, now)
    : null;
  if (!awaitsImport(handback, workflowKind)) {
    const writtenNote = written ? ` It was written ${written}.` : "";
    return {
      heading: "Cleaned copy",
      sentence: `The cleaned copy is in the output folder, at ${handback.output_path}.${writtenNote}`,
      meaning: "done",
    };
  }
  const when = written ? ` ${written}` : "";
  return {
    heading: "Handed back",
    sentence: `Weir put the cleaned copy at ${handback.output_path}${when} for your media manager to import. No media manager has said it imported it yet.`,
    meaning: "todo",
  };
}

/**
 * Whether the copy Weir handed back is still waiting for a media manager to take it: no manager has answered and
 * Weir has not settled it. That is ordinary waiting, nothing for a person to do yet. A Weir-only workflow has no
 * manager to wait for, so its copy is never waiting.
 */
export function awaitsImport(
  handback: ProcessingFileHandback | null | undefined,
  workflowKind: WorkflowKind["kind"],
): boolean {
  return (
    handback != null &&
    !handback.outcome &&
    !(handback.settled_at && handback.release_note) &&
    workflowKind === "linked"
  );
}

/** The kind of workflow a file belongs to; null until the workflows are known, so nothing is said wrongly meanwhile. */
export type WorkflowKindOf = (
  file: Pick<ProcessingFile, "library_id">,
) => WorkflowKind["kind"] | null;

/** A workflow that has since been removed counts as linked: its files were handed to a manager. */
export function workflowKindLookup(
  workflows:
    | readonly Pick<ProcessingLibrary, "id" | "manager_connection_ids">[]
    | undefined,
): WorkflowKindOf {
  return (file) => {
    if (!workflows) return null;
    const workflow = workflows.find((item) => item.id === file.library_id);
    return workflow ? workflowKindName(workflow) : "linked";
  };
}

/** "Imported by Sonarr" once a media manager has said so, for the list. */
export function importedLabel(file: ProcessingFile): string | null {
  return file.handback?.outcome === "imported"
    ? `Imported by ${file.handback.outcome_by ?? "your media manager"}`
    : null;
}

/** The newest record that describes a pass over the file, skipping the cleanup notes some passes add. */
export function latestPass(
  entries: ProcessingFileLogEntry[],
): ProcessingFileLogEntry | null {
  return (
    entries.find(
      (entry) =>
        entry.detail.audio_after !== undefined ||
        entry.detail.outcome !== undefined,
    ) ?? null
  );
}
