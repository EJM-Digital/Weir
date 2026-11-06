import { Link } from "react-router-dom";

import { StatTile, StatUnit } from "../../../components/panels/stat-tile";
import type { Filter } from "../processing-filter";
import { useFittingRows } from "./fit-rows";
import {
  figureWords,
  lineWords,
  waitFraction,
  type NextItem,
} from "./next-model";

/** The most "then" lines the tile lists after the first thing. */
const THEN_LINES = 3;

/** What an empty tile says, for everything and for each kind of work it can be narrowed to. */
const NOTHING_SCHEDULED_WORDS: Record<Filter, string> = {
  all: "Switch on a workflow or a cleanup job.",
  download: "No new downloads are due to be looked for.",
  library: "No library cleaning is scheduled.",
};

type NextTileProps = {
  items: readonly NextItem[];
  now: number;
  paused: boolean;
  /** The kind of work the items were narrowed to, so an empty tile can say so. */
  filter?: Filter;
  /** The band keeps its three tiles across, so the tile is as tall as the band and lists only the rows that fit. */
  across?: boolean;
};

/**
 * The tile is one set of parts in every state, so pausing and resuming change their text and what shows, never
 * which elements there are: the countdown bar keeps its place and its width glides.
 */
function partsOf({
  items,
  now,
  paused,
  filter,
}: {
  items: readonly NextItem[];
  now: number;
  paused: boolean;
  filter: Filter;
}) {
  const [first, ...rest] = paused ? [] : items;
  const idle = paused
    ? "Running files finish first."
    : first
      ? ""
      : NOTHING_SCHEDULED_WORDS[filter];
  const fraction = (first && waitFraction(first, now)) ?? 0;
  return {
    figure: (
      <>
        <span
          data-testid={first ? "live-next-figure" : undefined}
          hidden={!paused && !first}
        >
          {paused ? "Paused" : first ? figureWords(first.at, now) : ""}
        </span>
        <StatUnit>
          {paused
            ? "nothing new starts"
            : first
              ? first.label
              : "nothing scheduled"}
        </StatUnit>
      </>
    ),
    body: (
      <div>
        <p className="mm-stat__idle" hidden={idle === ""}>
          {idle}
        </p>
        <div hidden={!first}>
          <span className="mm-next__bar" aria-hidden="true">
            <i style={{ width: `${Math.round(fraction * 100)}%` }} />
          </span>
          <ul className="mm-next__then" data-testid="live-next-then">
            {rest.slice(0, THEN_LINES).map((item) => (
              <li key={item.key} data-fit="">
                <Link to={item.to}>then {item.label}</Link>
                <span>{lineWords(item.at, now)}</span>
              </li>
            ))}
          </ul>
        </div>
      </div>
    ),
  };
}

/** The next thing Weir does on its own, with a countdown, and what comes after it. */
export function NextTile({
  items,
  now,
  paused,
  filter = "all",
  across = true,
}: NextTileProps) {
  const [bodyRef] = useFittingRows(across);
  const { figure, body } = partsOf({ items, now, paused, filter });
  return (
    <StatTile label="Next" aside="on its own" figure={figure} bodyRef={bodyRef}>
      {body}
    </StatTile>
  );
}
