/**
 * The Pipeline: the files on their way through Weir, as stations left to right (Incoming, Queued,
 * Analysing, Processing, Delivering). The stations are one arrow-shaped ribbon, each segment tinted in its
 * colour with its icon, name and count (busy ones stronger, with a moving sheen; empty ones dimmed). Under
 * it, equal columns with dashed dividers hold every file as one card of one size, in its station's column.
 *
 * There are always three rows of cards: on a short window the cards shrink (down to 60px, a one-line title
 * and a smaller tile) and on a tall one they grow, rather than losing a row. A card whose station changes
 * glides to its new column and a new one fades in. A station with more files than the three rows shows
 * three and ends with "and N more". On a phone the stations stack and the same cards flow one under another.
 */
import {
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type ReactElement,
} from "react";
import { Link } from "react-router-dom";

import { Panel } from "../../../components/panels/panel";
import type { ProcessingFile } from "../../../lib/processing/files-api";
import { classNames } from "../../../lib/ui/class-names";
import { plural } from "../../../lib/ui/mm-plural";
import { useElementSize } from "../../../lib/ui/use-element-size";
import { useRemPx } from "../../../lib/ui/use-rem-px";
import type { LeavingCard } from "../leaving-cards";
import type { Lanes } from "../processing-model";
import type { Filter } from "../processing-filter";
import type { Delivery } from "./delivery-flight";
import { fitDetails } from "./fit-details";
import type { PipelineCard } from "./pipeline-card-types";
import { PipelineCardView, type CardPlace } from "./pipeline-card";
import { buildPipelineCards, groupByStage } from "./pipeline-cards";
import { allDoneBy, pipelineCount } from "./pipeline-heading";
import {
  PIPELINE_ROWS,
  boardMode,
  cardSize,
  cardsBudget,
  lanesHeight,
} from "./pipeline-layout";
import {
  IN_PROGRESS_PATH,
  PIPELINE_STAGES,
  STAGE_LABEL,
  type PipelineStage,
} from "./pipeline-stages";
import { StationIcon } from "./station-icons";
import { useDeliveryFlight } from "./use-delivery-flight";
import { useStageChanges } from "./use-stage-changes";
import { useStillWhileResizing } from "./use-still-while-resizing";

/** The space under the lanes inside the board, in px. */
const LANES_BOTTOM_PX = 10;

/** What an empty board says, for everything and for each kind of work it can be narrowed to. */
const NOTHING_IN_PROGRESS_WORDS: Record<Filter, string> = {
  all: "Nothing in progress. New downloads appear here.",
  download: "No new downloads in progress.",
  library: "No library cleaning right now.",
};
const PAUSED = "Paused · nothing new starts.";

/** The station's tooltip: its name and its count in full, for a ribbon that is too narrow to show them whole. */
function stationTitle(stage: PipelineStage, count: number): string {
  const files = count === 0 ? "no files" : plural(count, "file", "files");
  return `${STAGE_LABEL[stage]}: ${files}`;
}

function Station({ stage, count }: { stage: PipelineStage; count: number }) {
  return (
    <div
      role="group"
      aria-label={`${STAGE_LABEL[stage]}: ${count}`}
      title={stationTitle(stage, count)}
      data-stage={stage}
      className={classNames(
        "mm-pipe__station",
        count > 0 ? "mm-pipe__station--active" : "mm-pipe__station--empty",
      )}
    >
      <span className="mm-pipe__station-body">
        <span aria-hidden="true" className="mm-pipe__dot">
          <StationIcon stage={stage} />
        </span>
        <span className="mm-pipe__station-text">
          <span className="mm-pipe__station-name">{STAGE_LABEL[stage]}</span>
          {count === 0 ? null : (
            <span className="mm-pipe__station-count">
              {count.toLocaleString()}
            </span>
          )}
        </span>
      </span>
    </div>
  );
}

/** What a screen reader hears when a file ends: it was delivered, or why it stopped. */
function announcementOf(cards: readonly PipelineCard[]): string {
  return cards
    .filter((card) => card.end !== null)
    .map((card) =>
      card.end === "delivered"
        ? `${card.title} was delivered. It moves to Just finished.`
        : `${card.title}: ${card.status.text}. ${card.details
            .slice(0, 1)
            .flatMap((line) => line.parts)
            .join("")}`.trim(),
    )
    .join(" ");
}

export type PipelineBoardProps = {
  /** The lanes before any filter: the board applies the filter itself. */
  lanes: Lanes;
  /** Queued files the lanes hold no card for, because the list they come from stops at its limit. */
  queuedMore?: number;
  /** The cards of files that have just ended, before any filter. */
  leaving: readonly LeavingCard[];
  filter: Filter;
  /** The clock, in ms, which the countdowns on the cards count against. */
  now: number;
  /** Weir is paused, so nothing new starts. */
  paused?: boolean;
  /** Opens a file's story. */
  onOpen: (file: ProcessingFile) => void;
  /** The board fills the grid cell it is in: its lanes take the height left under the stations, and the cards are sized to it. */
  fill?: boolean;
  /** Without a grid cell (the page scrolls): the tallest the whole board may be, in px. It is only a ceiling. */
  budget?: number;
  /** A delivered card's tile is leaving. By default it flies to the file's tile on the Just finished shelf. */
  onDelivered?: (delivery: Delivery) => void;
};

export function PipelineBoard({
  lanes,
  queuedMore = 0,
  leaving,
  filter,
  now,
  paused = false,
  fill = false,
  budget,
  onOpen,
  onDelivered,
}: PipelineBoardProps): ReactElement {
  const cards = useMemo(
    () => buildPipelineCards(lanes, leaving, filter, now),
    [lanes, leaving, filter, now],
  );
  const endedKeys = useMemo(
    () => new Set(leaving.map((entry) => entry.key)),
    [leaving],
  );
  const arrived = useStageChanges(cards);
  useStillWhileResizing();

  const stations = PIPELINE_STAGES.length;
  const [bodyRef, body] = useElementSize<HTMLDivElement>();
  const [lanesRef, lanesBox] = useElementSize<HTMLDivElement>();
  const rem = useRemPx();
  const stacked = boardMode(body.width, stations, rem) === "stacked";
  const boardRef = useRef<HTMLDivElement>(null);
  // What sits above the lanes (header, stations): measured, so a page that scrolls can count the rows its budget
  // holds in real pixels. It only changes when the board changes shape.
  const [chrome, setChrome] = useState(0);
  useLayoutEffect(() => {
    const panel = boardRef.current?.closest("section");
    const lanes = lanesRef.current;
    if (!panel || !lanes || fill) return;
    const next = Math.round(
      lanes.getBoundingClientRect().top - panel.getBoundingClientRect().top,
    );
    setChrome(next);
  }, [fill, stacked, lanesRef]);
  const room = stacked
    ? undefined
    : fill
      ? lanesBox.height > 0
        ? lanesBox.height
        : undefined
      : cardsBudget(budget, chrome + LANES_BOTTOM_PX);
  const size = cardSize(room);
  useDeliveryFlight(boardRef, cards, endedKeys, onDelivered);

  const grouped = groupByStage(cards);
  const live = cards.filter((card) => card.end === null);
  // Queued files past the first page of files have no card; they are counted, not drawn, and only where downloads show.
  const uncarded = (stage: PipelineStage) =>
    stage === "queued" && filter !== "library" ? queuedMore : 0;
  const countAt = (stage: PipelineStage) =>
    live.filter((card) => card.stage === stage).length + uncarded(stage);
  const places = new Map<string, CardPlace>();
  const more: { stage: PipelineStage; left: number; titles: string }[] = [];
  PIPELINE_STAGES.forEach((stage, column) => {
    const here = grouped[stage];
    here.forEach((card, row) =>
      places.set(card.key, {
        column,
        stations,
        top: Math.min(row, PIPELINE_ROWS - 1) * size.step,
        hidden: row >= PIPELINE_ROWS,
      }),
    );
    if (here.length > PIPELINE_ROWS) {
      more.push({
        stage,
        left: here.length - PIPELINE_ROWS,
        titles: here.map((card) => card.title).join(", "),
      });
    }
  });
  // One stable order for every card, so a card that changes station keeps its element and glides instead of being re-made.
  const everyCard = [...cards].sort((a, b) => (a.key < b.key ? -1 : 1));

  // The detail block of each card shows as many whole lines as the card's height holds: measured after every change, and again once the fonts are in.
  useLayoutEffect(() => {
    const host = lanesRef.current ?? bodyRef.current;
    /* istanbul ignore if -- @preserve */
    if (!host) return undefined;
    fitDetails(host);
    let current = true;
    const again = () => {
      if (current) fitDetails(host);
    };
    void document.fonts?.ready.then(again);
    document.fonts?.addEventListener?.("loadingdone", again);
    return () => {
      current = false;
      document.fonts?.removeEventListener?.("loadingdone", again);
    };
  });

  const calm = live.length === 0 && cards.length === 0;
  const calmLine = paused ? PAUSED : NOTHING_IN_PROGRESS_WORDS[filter];
  const vars: CSSProperties = {
    ["--pipe-n" as string]: stations,
    ["--pipe-card-h" as string]: `${size.card}px`,
    ["--pipe-tile-h" as string]: `${size.tile}px`,
    ["--pipe-title-lines" as string]: size.oneLine ? 1 : 2,
  };
  const laneStyle: CSSProperties = fill
    ? { flex: "1 1 0", marginBottom: LANES_BOTTOM_PX }
    : {
        height: room ?? lanesHeight(size),
        flex: "none",
        marginBottom: LANES_BOTTOM_PX,
      };

  return (
    <Panel
      title="Pipeline"
      count={pipelineCount(
        live.length + uncarded("queued"),
        allDoneBy(lanes, filter, now),
      )}
    >
      <div ref={boardRef} className="mm-pipe" data-testid="pipeline-board">
        <div
          ref={bodyRef}
          className={classNames(
            "mm-pipe__body",
            stacked && "mm-pipe__body--stacked",
          )}
          style={vars}
        >
          {stacked ? (
            <>
              {calm ? <p className="mm-pipe__calm">{calmLine}</p> : null}
              {PIPELINE_STAGES.map((stage) =>
                grouped[stage].length === 0 ? null : (
                  <div key={stage} data-station={stage}>
                    <Station stage={stage} count={countAt(stage)} />
                    {grouped[stage].map((card) => (
                      <PipelineCardView
                        key={card.key}
                        card={card}
                        arrived={arrived.has(card.key)}
                        onOpen={onOpen}
                      />
                    ))}
                  </div>
                ),
              )}
            </>
          ) : (
            <>
              <div className="mm-pipe__stations">
                {PIPELINE_STAGES.map((stage) => (
                  <Station key={stage} stage={stage} count={countAt(stage)} />
                ))}
              </div>
              <div ref={lanesRef} className="mm-pipe__lanes" style={laneStyle}>
                {PIPELINE_STAGES.slice(1).map((stage, index) => (
                  <div
                    key={stage}
                    aria-hidden="true"
                    className="mm-pipe__divider"
                    style={{ left: `${((index + 1) * 100) / stations}%` }}
                  />
                ))}
                {calm ? <p className="mm-pipe__calm">{calmLine}</p> : null}
                {everyCard.map((card) => (
                  <PipelineCardView
                    key={card.key}
                    card={card}
                    place={places.get(card.key)}
                    arrived={arrived.has(card.key)}
                    onOpen={onOpen}
                  />
                ))}
                {more.map((line) => (
                  <Link
                    key={line.stage}
                    to={IN_PROGRESS_PATH}
                    title={`${STAGE_LABEL[line.stage]}: ${line.titles}`}
                    className="mm-pipe__more"
                    style={{
                      left: `calc(${PIPELINE_STAGES.indexOf(line.stage)} * 100% / ${stations} + 8px)`,
                      top: PIPELINE_ROWS * size.step,
                    }}
                  >
                    and {line.left.toLocaleString()} more →
                  </Link>
                ))}
              </div>
            </>
          )}
        </div>
        <p
          className="sr-only"
          role="status"
          aria-live="polite"
          data-testid="pipeline-announcement"
        >
          {announcementOf(cards)}
        </p>
      </div>
    </Panel>
  );
}
