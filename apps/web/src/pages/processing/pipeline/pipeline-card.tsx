/**
 * One card of the Pipeline. Every card is one template, the same size wherever it is:
 *
 *   tile (2:3, as tall as the card allows) | title (one or two lines)
 *                                          | one status line (the lead bold in the station's colour)
 *                                          | a thin bar, moving while work is under way
 *                                          | detail lines: as many whole ones as the card's height holds
 *
 * The station's colour is the card's left edge. A delivered card is full and green for a moment before its
 * tile flies to Just finished; a card whose file stopped is edged in the colour of what happened. Clicking a
 * card opens the file's story, as every card on the page does; a library clean has no file to open.
 */
import type { CSSProperties, ReactElement } from "react";

import { StatusDot } from "../../../components/panels/status-dot";
import { Poster } from "../../../components/shared/poster";
import { classNames } from "../../../lib/ui/class-names";
import { motionAllowed } from "../../../lib/ui/motion-allowed";
import type { StatusMeaning } from "../../../lib/ui/status-meaning";
import type { ProcessingFile } from "../../../lib/processing/files-api";
import { FitText } from "../../../lib/ui/fit-text";
import {
  CARD_ATTRIBUTE,
  DETAILS_ATTRIBUTE,
  KEEP_ATTRIBUTE,
} from "./fit-details";
import type {
  CardEnd,
  CardStatus,
  DetailLine,
  DetailPart,
  PipelineCard as Card,
} from "./pipeline-card-types";
import { TILE_ATTRIBUTE } from "./use-delivery-flight";

/** Where a card sits on the lanes: its station's column and its row. */
export type CardPlace = {
  column: number;
  stations: number;
  /** The row's top, in px. */
  top: number;
  /** Beyond the rows that fit: kept in place (so it can glide back) but not shown. */
  hidden: boolean;
};

/** What a card that has left the board says by its edge: delivered is done, stopped is broken, a rejection needs a look. */
const END_MEANING: Record<CardEnd, StatusMeaning> = {
  delivered: "done",
  failed: "broken",
  rejected: "attention",
};

/** The words after the bold lead, the lead's own space left to the lead's margin so every wording is measured as drawn. */
function afterLead(words: string, lead: string | null): string {
  return lead && words.startsWith(lead)
    ? words.slice(lead.length).trimStart()
    : words;
}

/** The status line says the fullest of its wordings that the card's width holds; the card's tooltip has them whole. */
function StatusLine({ status }: { status: CardStatus }) {
  const lead =
    status.lead && status.text.startsWith(status.lead) ? status.lead : null;
  const wordings = [status.text, ...(status.fits ?? [])];
  return (
    <span className="mm-pipe__status" data-status={status.meaning}>
      <StatusDot
        meaning={status.meaning}
        className={status.pulse ? "mm-pipe__pulse" : undefined}
      />
      <span className="mm-pipe__status-text">
        {lead ? <b>{lead}</b> : null}
        <FitText
          className="mm-pipe__status-rest"
          words={wordings.map((words) => afterLead(words, lead))}
        />
      </span>
    </span>
  );
}

const partText = (part: DetailPart) =>
  typeof part === "string" ? part : part.bold;

/** A line that has briefer wordings says the fullest the card's width holds, its lead plain and the rest bold. */
function FittingParts({
  parts,
  fits,
}: {
  parts: readonly DetailPart[];
  fits: readonly string[];
}) {
  const lead = typeof parts[0] === "string" ? parts[0] : "";
  return (
    <FitText
      className="block"
      words={[parts.map(partText).join(""), ...fits]}
      format={(words) =>
        lead && words.startsWith(lead) ? (
          <>
            {lead}
            <b>{words.slice(lead.length)}</b>
          </>
        ) : (
          <b>{words}</b>
        )
      }
    />
  );
}

function Detail({ line }: { line: DetailLine }) {
  return (
    <span className="mm-pipe__detail">
      <span className={line.mono ? "mm-pipe__file" : undefined}>
        {line.fits ? (
          <FittingParts parts={line.parts} fits={line.fits} />
        ) : (
          line.parts.map((part, index) =>
            typeof part === "string" ? (
              <span key={index}>{part}</span>
            ) : (
              <b key={index}>{part.bold}</b>
            ),
          )
        )}
      </span>
      <span>{line.right}</span>
    </span>
  );
}

/** The card's tooltip: its title, what is happening, then every fact it holds, a line each. */
export function tooltip(card: Card): string {
  return [card.title, card.status.text, ...card.fullFacts].join("\n");
}

/** What a screen reader says for a card: the same facts as the tooltip, as sentences. */
export function spokenName(card: Card): string {
  const facts = card.fullFacts.map((fact) => fact.replace(/\.$/, ""));
  return [`${card.title}: ${card.status.text}`, ...facts].join(". ");
}

export function PipelineCardView({
  card,
  place,
  arrived,
  onOpen,
}: {
  card: Card;
  place?: CardPlace;
  /** The card has just changed station, and glows as it arrives. */
  arrived: boolean;
  onOpen: (file: ProcessingFile) => void;
}): ReactElement {
  const style: CSSProperties = {};
  if (place) {
    style.left = `calc(${place.column} * 100% / ${place.stations} + 6px)`;
    style.top = `${place.top}px`;
  }
  const className = classNames(
    "mm-pipe__card",
    card.end === "delivered" && "mm-pipe__card--delivered",
    place?.hidden && "mm-pipe__card--hidden",
    arrived && motionAllowed() && "mm-pipe__card--arrived",
  );
  const common = {
    className,
    style,
    title: tooltip(card),
    "aria-label": spokenName(card),
    "aria-hidden": place?.hidden ? true : undefined,
    "data-stage": card.stage,
    "data-status": card.end ? END_MEANING[card.end] : undefined,
    [CARD_ATTRIBUTE]: "",
  };
  const body = (
    <>
      <span {...{ [TILE_ATTRIBUTE]: card.key }} className="mm-pipe__tile">
        <Poster
          url={card.file?.poster_url}
          title={card.title}
          workflow={card.workflow}
        />
      </span>
      <span className="mm-pipe__text">
        <span className="mm-pipe__title">{card.title}</span>
        <StatusLine status={card.status} />
        {card.bar ? (
          <span
            aria-hidden="true"
            data-status={card.status.meaning}
            className={classNames(
              "mm-pipe__bar",
              card.bar.moving && "mm-pipe__bar--moving",
              card.bar.waiting && "mm-pipe__bar--waiting",
            )}
          >
            <i style={{ width: `${card.bar.width}%` }} />
          </span>
        ) : null}
        {card.details.length > 0 ? (
          <span
            className="mm-pipe__details"
            {...{ [DETAILS_ATTRIBUTE]: "", [KEEP_ATTRIBUTE]: card.keep }}
          >
            {card.details.map((line, index) => (
              <Detail key={index} line={line} />
            ))}
          </span>
        ) : /* istanbul ignore next -- @preserve */ null}
      </span>
    </>
  );
  const { file } = card;
  return file ? (
    <button
      type="button"
      {...common}
      tabIndex={place?.hidden ? -1 : undefined}
      onClick={() => onOpen(file)}
    >
      {body}
    </button>
  ) : (
    <div role="group" {...common}>
      {body}
    </div>
  );
}
