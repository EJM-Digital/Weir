import { Link } from "react-router-dom";

import { StatTile, StatUnit } from "../../../components/panels/stat-tile";
import type { ProcessingFile } from "../../../lib/processing/files-api";
import { IN_PROGRESS_PATH } from "../pipeline/pipeline-stages";
import type { Filter } from "../processing-filter";
import type { WorkingItem } from "../processing-model";
import { useFittingRows } from "./fit-rows";
import { FitText } from "../../../lib/ui/fit-text";
import { STEP_WORDS, moreWords, waitWords } from "./working-words";
import { setupTabPath } from "../../../lib/settings/setup-areas";

/** The most files the tile lists before saying how many more there are; how many show is up to its height. */
const MOST_OPERATIONS = 4;

/** Where a person changes how many files Weir works on at once, and how long a new download waits. */
const PERFORMANCE_PATH = setupTabPath("speed");

/** What an empty tile says, for everything and for each kind of work it can be narrowed to. */
const IDLE_WORDS: Record<Filter, string> = {
  all: "Nothing being cleaned.",
  download: "No new downloads being cleaned.",
  library: "No library cleaning right now.",
};

type WorkingTileProps = {
  working: readonly WorkingItem[];
  /** The most files Weir works on at once, or null while it is not known. */
  filesAtOnce: number | null;
  /** How long a new download is left alone, when every workflow agrees. */
  waitSeconds: number | null;
  /** The kind of work the files were narrowed to, so an empty tile can say so. */
  filter?: Filter;
  /** The band keeps its three tiles across, so the tile is as tall as the band and lists only the rows that fit. */
  across?: boolean;
  onOpen: (file: ProcessingFile) => void;
};

function Operation({
  item,
  onOpen,
}: {
  item: WorkingItem;
  onOpen: (file: ProcessingFile) => void;
}) {
  const percent =
    item.percent === null
      ? null
      : Math.round(Math.min(100, Math.max(0, item.percent)));
  const content = (
    <>
      <span className="mm-op__text flex gap-1">
        <b className="min-w-0 truncate" title={item.name}>
          {item.name}
        </b>{" "}
        <span className="shrink-0">· {STEP_WORDS[item.step]}</span>
      </span>
      <span className="mm-op__right">
        {percent === null ? "" : `${percent}%`}
      </span>
      <span className="mm-op__bar" data-status="doing" aria-hidden="true">
        <i
          className={percent === null ? "mm-op__bar--moving" : undefined}
          style={percent === null ? undefined : { width: `${percent}%` }}
        />
      </span>
    </>
  );
  const { file } = item;
  return (
    <li>
      {file ? (
        <button
          type="button"
          className="mm-op"
          onClick={() => onOpen(file)}
          data-fit=""
        >
          {content}
        </button>
      ) : (
        <div className="mm-op" data-fit="">
          {content}
        </div>
      )}
    </li>
  );
}

/** The files Weir is writing right now, each with its step and how far it has got. */
export function WorkingTile({
  working,
  filesAtOnce,
  waitSeconds,
  filter = "all",
  across = true,
  onOpen,
}: WorkingTileProps) {
  const [bodyRef, fits] = useFittingRows(across);
  const shown = working.slice(0, MOST_OPERATIONS);
  const more = working.length - Math.min(fits, shown.length);
  return (
    <StatTile
      label="Working on now"
      to={IN_PROGRESS_PATH}
      linkName="Working on now: every file in Activity"
      bodyRef={bodyRef}
      aside={
        <span className="mm-aside">
          {more > 0 ? (
            <span className="mm-aside__more">
              <Link
                to={IN_PROGRESS_PATH}
                aria-label={`${more.toLocaleString()} more in Activity`}
              >
                <FitText
                  className="mm-aside__more-words"
                  words={moreWords(more)}
                />
              </Link>
              <span aria-hidden="true">{" · "}</span>
            </span>
          ) : null}
          <Link to={PERFORMANCE_PATH}>Change</Link>
        </span>
      }
      figure={
        <>
          <span data-testid="live-working-count">
            {working.length.toLocaleString()}
          </span>
          <StatUnit>
            {filesAtOnce === null
              ? "at once"
              : `of ${filesAtOnce.toLocaleString()} at once`}
          </StatUnit>
          {waitSeconds === null ? null : (
            <FitText
              className="mm-stat__note"
              words={waitWords(waitSeconds)}
              title={`New downloads wait ${waitSeconds} seconds after they stop changing.`}
            />
          )}
        </>
      }
    >
      {/* Both are always there and one is hidden, so the list keeps its element when it empties and fills. */}
      <p className="mm-stat__idle" hidden={working.length > 0}>
        {IDLE_WORDS[filter]}
      </p>
      <ul
        className="mm-ops"
        data-testid="live-working"
        hidden={working.length === 0}
      >
        {shown.map((item) => (
          <Operation key={item.key} item={item} onOpen={onOpen} />
        ))}
      </ul>
    </StatTile>
  );
}
