/**
 * What each card on the Pipeline says, station by station. Every line comes from a reading the server
 * gave (a hold's own clock, the pass's progress, the probe of the file); a line with no real value is left
 * out rather than guessed, and a detail line never says what the status line already says.
 */
import { baseName } from "../../../lib/format/path";
import { narrowing } from "../../../lib/ui/fit-text";
import type { StatusMeaning } from "../../../lib/ui/status-meaning";
import { plural } from "../../../lib/ui/mm-plural";
import {
  arrivingDeadline,
  secondsLeft,
  type ArrivingItem,
  type HandingItem,
  type WaitingItem,
  type WorkingItem,
  type WorkSource,
} from "../processing-model";
import {
  clock,
  removedTrackWords,
  ringFraction,
  ringState,
  timeLeft,
  timeRemaining,
  workingFigures,
} from "../processing-words";
import { holdWords, outOfScheduleWords } from "../reason-words";
import type { FlowStepId } from "../stage-flow-model";
import type {
  CardBar,
  CardStatus,
  CardWords,
  DetailLine,
  DetailPart,
} from "./pipeline-card-types";
import {
  SOURCE_LABEL,
  arrivingNote,
  identityFacts,
  placeFact,
  workingFacts,
} from "./card-facts";
import type { WorkingStage } from "./pipeline-stages";

const DEFAULT_WORKFLOW = "Workflow";

/** A bar that only says the pass is moving: no figure says how far it has got. */
const MOVING_BAR: CardBar = { width: 35, waiting: true, moving: true };

const bold = (text: string): DetailPart => ({ bold: text });

/** More facts than a probe ever measures, so that the first of them always matters most. */
const FACTS_COUNT = 10;

/** The briefer words a status falls back on in a card too narrow for its wording, never the front of a longer one cut off. */
const BRIEFLY: Readonly<Record<string, readonly string[]>> = {
  "Checking now": ["Checking", "Check"],
  "Checking file": ["Checking", "Check"],
  "Still importing": ["Importing", "Import"],
  "Waiting to settle": ["Waiting"],
  "Looking again later": ["Later"],
  "Can't open it yet": ["Can't open", "Locked"],
  "Can't write output": ["Can't write", "No write"],
  "Waiting for space": ["No space", "Full"],
  "Waiting its turn": ["Waiting"],
  "Outside its hours": ["Closed"],
  "Writing copy": ["Writing"],
  Planning: ["Plan"],
  Cleaning: ["Clean"],
  Verifying: ["Verify"],
  "Replacing file": ["Replacing"],
  "Handing back": ["Handing", "Moving"],
  "Couldn't finish": ["Failed"],
};

/** A status that is words alone, with the briefer words it falls back on where it has them. */
function said(
  text: string,
  meaning: StatusMeaning,
  pulse: boolean,
): CardStatus {
  const briefly = BRIEFLY[text];
  return briefly
    ? { text, meaning, pulse, fits: briefly }
    : { text, meaning, pulse };
}

/** "Download · Movies": where the file came from and which workflow has it. */
function sourceLine(source: WorkSource, workflow: string): DetailLine {
  return { parts: [bold(SOURCE_LABEL[source]), ` · ${workflow}`] };
}

/** The file's own name, in the monospace line at the foot of a card. */
function fileLine(path: string): DetailLine | null {
  const name = baseName(path);
  return name ? { parts: [name], mono: true } : null;
}

/** The facts the last probe measured, with a quiet remark at the right. A library clean has no facts, only the remark. */
function factsLine(facts: string, right?: string): DetailLine {
  if (!facts) return { parts: [], right };
  // The first fact matters most ("1080p"), the last least ("2.27 GB"): a narrow card drops from the end.
  const [, ...fits] = narrowing(
    facts
      .split(" · ")
      .map((text, index) => ({ text, matters: FACTS_COUNT - index })),
  );
  return { parts: [bold(facts)], fits, right };
}

/** What a pass is taking out, with the count of tracks alone for a card too narrow to name them. */
function removingLine(removed: string[], tracks: number): DetailLine {
  return {
    parts: ["Removing ", bold(removed.join(", "))],
    fits: [`Removing ${plural(tracks, "track", "tracks")}`],
  };
}

function present(lines: ReadonlyArray<DetailLine | null>): DetailLine[] {
  return lines.filter((line): line is DetailLine => line !== null);
}

function footer(
  source: WorkSource,
  workflow: string,
  path: string,
): DetailLine[] {
  return present([fileLine(path), sourceLine(source, workflow)]);
}

/** The workflow that has an arriving file: its own name for itself, or the plain word when none is known. */
export function arrivingWorkflow(item: ArrivingItem): string {
  return item.file.library_name || DEFAULT_WORKFLOW;
}

export function incomingWords(item: ArrivingItem, now: number): CardWords {
  const workflow = arrivingWorkflow(item);
  const left = secondsLeft(arrivingDeadline(item), now);
  const checking = ringState(left) === "checking";
  const fraction = ringFraction(item, left);
  const status = checking
    ? said("Checking now", "doing", true)
    : said(waitingWords(item), "todo", false);
  const timing =
    ringState(left) === "counting" && left != null
      ? `${item.holdUntil != null ? "ready in" : "looks again in"} ${clock(left)}`
      : undefined;
  return {
    status,
    bar:
      fraction == null
        ? null
        : { width: Math.round(fraction * 100), waiting: false, moving: false },
    details: present([
      factsLine(item.facts, timing),
      ...footer("download", workflow, item.path),
    ]),
    fullFacts: [
      arrivingNote(item, now),
      item.facts,
      ...identityFacts("download", workflow, item.path),
    ].filter(Boolean),
  };
}

/** What an arriving file waits for, in a few words: a media manager still importing it, or the kind of hold it is on. */
function waitingWords(item: ArrivingItem): string {
  if (item.upstream) return "Still importing";
  if (item.file.source_gone) return "No longer there";
  return item.note ? holdWords(item.note) : "Waiting";
}

export function queuedWords(item: WaitingItem, place: number): CardWords {
  const line = factsLine(
    item.source === "library" ? "" : item.facts,
    `#${place} in line`,
  );
  return {
    status: said(
      item.note ? outOfScheduleWords(item.note) : "Waiting its turn",
      "todo",
      false,
    ),
    bar: null,
    details: present([
      line,
      ...footer(item.source, item.libraryName, item.path),
    ]),
    fullFacts: [
      placeFact(place),
      item.note ?? "",
      item.source === "library" ? "" : item.facts,
      ...identityFacts(item.source, item.libraryName, item.path),
    ].filter(Boolean),
  };
}

const ANALYSING_TEXT: Partial<Record<FlowStepId, string>> = {
  checking: "Checking file",
  plan: "Planning",
};

function analysingWords(item: WorkingItem): CardWords {
  const running = workingFigures(item).running;
  return {
    status: said(ANALYSING_TEXT[item.step] ?? "Checking file", "doing", true),
    bar: MOVING_BAR,
    details: present([
      factsLine(item.facts, running ? `running ${running}` : undefined),
      ...footer(item.source, item.libraryName, item.path),
    ]),
    fullFacts: workingFullFacts(item),
  };
}

/** A pass's own figures, then what it is, which workflow has it and the file's name. */
function workingFullFacts(item: WorkingItem): string[] {
  return [
    ...workingFacts(item),
    item.facts,
    ...identityFacts(item.source, item.libraryName, item.path),
  ].filter(Boolean);
}

/** A writing pass: its percent and, where the server has an estimate, how long it has to go, in fewer words as the card narrows. */
function writingStatus(percent: number, etaSeconds: number | null): CardStatus {
  const lead = `${Math.floor(percent)}%`;
  const left = timeLeft(etaSeconds);
  const base = { lead, meaning: "doing", pulse: false } as const;
  if (!left) return { ...base, text: `${lead} writing`, fits: [lead] };
  return {
    ...base,
    text: `${lead} · ${left}`,
    fits: [`${lead} · ${timeRemaining(etaSeconds)}`, lead],
  };
}

function writingWords(item: WorkingItem, percent: number): CardWords {
  const figures = workingFigures(item);
  const removed = removedTrackWords(item);
  return {
    status: writingStatus(percent, item.etaSeconds),
    bar: { width: Math.floor(percent), waiting: false, moving: true },
    // The most telling first, one fact a line so none is squeezed by another: the status line has the percent and
    // the time left, then come the speed, what is being taken out, what the file is, how far through it the pass is,
    // how fast it is read, how long it has run, and last the file's own name.
    details: present([
      figures.speed ? { parts: ["Speed ", bold(figures.speed)] } : null,
      removed.length > 0
        ? removingLine(removed, item.removedAudio + item.removedSubtitles)
        : null,
      factsLine(item.facts),
      figures.through ? { parts: [bold(figures.through)] } : null,
      figures.reading ? { parts: ["Reading ", bold(figures.reading)] } : null,
      figures.running ? { parts: ["Running ", bold(figures.running)] } : null,
      ...footer(item.source, item.libraryName, item.path),
    ]),
    fullFacts: workingFullFacts(item),
  };
}

/** What a pass says while it has no percentage to show. */
function unmeasuredProcessingText(item: WorkingItem): string {
  if (item.step === "verify") return "Verifying";
  return item.source === "library" ? "Cleaning" : "Writing copy";
}

function processingWords(item: WorkingItem): CardWords {
  if (
    item.source === "download" &&
    item.step === "write" &&
    item.percent != null
  ) {
    return writingWords(item, item.percent);
  }
  const running = workingFigures(item).running;
  return {
    status: said(unmeasuredProcessingText(item), "doing", true),
    bar: MOVING_BAR,
    details: present([
      item.source === "download"
        ? factsLine(item.facts, running ? `running ${running}` : undefined)
        : null,
      ...footer(item.source, item.libraryName, item.path),
    ]),
    fullFacts: workingFullFacts(item),
  };
}

export function deliveringWords(item: HandingItem | WorkingItem): CardWords {
  return {
    status: said(
      item.source === "library" ? "Replacing file" : "Handing back",
      "doing",
      true,
    ),
    bar: MOVING_BAR,
    details: footer(item.source, item.libraryName, item.path),
    fullFacts: [
      ...("facts" in item ? workingFacts(item) : []),
      ...identityFacts(item.source, item.libraryName, item.path),
    ],
  };
}

/** A pass that is on a step of its stages: Analysing, Processing or Delivering by the step. */
export function workingWords(
  item: WorkingItem,
  stage: WorkingStage,
): CardWords {
  if (stage === "analysing") return analysingWords(item);
  return stage === "processing" ? processingWords(item) : deliveringWords(item);
}

export const DELIVERED_LEAD = "✓ Delivered";

/** A card that has just been delivered: full and green, before it flies to Just finished. */
export function deliveredWords(
  source: WorkSource,
  workflow: string,
  path: string,
): CardWords {
  return {
    status: {
      text: DELIVERED_LEAD,
      lead: DELIVERED_LEAD,
      meaning: "done",
      pulse: false,
    },
    bar: { width: 100, waiting: false, moving: false },
    details: footer(source, workflow, path),
    fullFacts: identityFacts(source, workflow, path),
  };
}

/** A card whose file stopped, in the station it stopped at, with a few words on why. A rejection is a decision, so it is not drawn as a failure. */
export function stoppedWords(
  kind: "failed" | "rejected",
  reason: string,
  source: WorkSource,
  workflow: string,
  path: string,
): CardWords {
  return {
    status: said(
      kind === "failed" ? "Couldn't finish" : "Rejected",
      kind === "failed" ? "broken" : "attention",
      false,
    ),
    bar: null,
    details: present([{ parts: [reason] }, ...footer(source, workflow, path)]),
    keep: 1,
    fullFacts: [reason, ...identityFacts(source, workflow, path)],
  };
}
