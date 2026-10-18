/**
 * Just finished: the files Weir has recently handed back, as one shelf of 2:3 tiles, newest first. Chips
 * in the panel's header narrow it to one workflow, and its description counts what that choice finished today.
 * Each
 * tile is tagged with its workflow, and the tag goes to that workflow's library. Clicking a tile opens the
 * file's story.
 *
 * Tiles are sized from the height of the shelf, exactly 2:3, and only whole tiles are drawn.
 */
import {
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type ReactElement,
} from "react";

import { Panel } from "../../../components/panels/panel";
import { loadErrorMessage } from "../../../lib/api/error-message";
import type { FinishedFile } from "../../../lib/activity/processing-outcome";
import { useElementSize } from "../../../lib/ui/use-element-size";
import type { Filter } from "../processing-filter";
import { useFinishedAnnouncement } from "../use-finished-files";
import { canvasMeasure, type MeasureText } from "../../../lib/ui/measure-text";
import { statusFont } from "./status-line";
import {
  shelfFit,
  shelfRowNeed,
  tilesAcross,
  type ShelfFit,
} from "./shelf-layout";
import { ShelfFilter } from "./shelf-filter";
import { shelfOf, todayWords } from "./shelf-model";
import { ShelfTiles } from "./shelf-tiles";
import { readShelfWorkflow, saveShelfWorkflow } from "./shelf-workflow-choice";

const NO_NAMES: ReadonlyMap<number, string> = new Map();

/** What an empty shelf says, for everything and for each kind of work it can be narrowed to. */
const NOTHING_FINISHED_WORDS: Record<Filter, string> = {
  all: "Nothing has finished yet.",
  download: "No new downloads have finished yet.",
  library: "No library cleaning has finished yet.",
};

type Fit = ShelfFit & { across: number };

export type JustFinishedShelfProps = {
  /** The newest finished files, before any filter. */
  items: readonly FinishedFile[];
  /** Why they could not be read, when the read failed: an empty shelf then says so instead of claiming a quiet day. */
  readError?: unknown;
  filter: Filter;
  /** Narrows the shelf to this workflow and leaves its chip the only one; the owner's own choice of chip otherwise. */
  workflowId?: number | null;
  now: number;
  /** The names of the workflows, by id: each file's tile carries its workflow's name and colour. */
  workflowNames?: ReadonlyMap<number, string>;
  /** The workflows that have a chip; every named workflow when left out. */
  enabledWorkflowIds?: ReadonlySet<number>;
  /** What the day adds up to across every workflow, when the page knows better than the loaded files tell. */
  count?: string;
  /** Opens a finished file's story. */
  onOpen: (item: FinishedFile) => void;
  /**
   * Told the height of this whole panel that its tiles can use, whenever it changes: any more would leave empty
   * space under the tiles, which the page can give to something else.
   */
  onHeightNeed?: (px: number) => void;
  /** Measures a line in the caption's font, in place of the canvas the page measures with. */
  measureText?: MeasureText;
};

export function JustFinishedShelf({
  items,
  readError = null,
  filter,
  workflowId = null,
  now,
  workflowNames = NO_NAMES,
  enabledWorkflowIds,
  count,
  onOpen,
  onHeightNeed,
  measureText,
}: JustFinishedShelfProps): ReactElement {
  const [chosen, setChosen] = useState<number | null>(readShelfWorkflow);
  const chips = useMemo(
    () =>
      [...workflowNames]
        .filter(([id]) => enabledWorkflowIds?.has(id) ?? true)
        .map(([id, name]) => ({ id, name })),
    [workflowNames, enabledWorkflowIds],
  );
  const picked = chips.some((chip) => chip.id === chosen) ? chosen : null;
  const narrowedTo = workflowId ?? picked;
  const choices =
    workflowId === null
      ? [{ id: null, name: "All" }, ...chips]
      : chips.filter((chip) => chip.id === workflowId);
  const shelf = useMemo(
    () =>
      shelfOf(items, { filter, workflowId: narrowedTo, now }, workflowNames),
    [items, filter, narrowedTo, now, workflowNames],
  );
  const announcement = useFinishedAnnouncement(items);
  const [shelfRef, shelfSize] = useElementSize<HTMLDivElement>();
  const box = useRef<HTMLUListElement>(null);
  const [fit, setFit] = useState<Fit | null>(null);
  // The tile size comes from the tiles' own box, before paint, so nothing shifts.
  useLayoutEffect(() => {
    const tiles = box.current;
    if (!tiles || !(tiles.clientHeight > 0)) return;
    const size = shelfFit(tiles.clientHeight, tiles.clientWidth);
    const next: Fit = {
      ...size,
      across: tilesAcross(tiles.clientWidth, size.width),
    };
    setFit((current) =>
      current &&
      current.width === next.width &&
      current.caption === next.caption &&
      current.across === next.across
        ? current
        : next,
    );
    // The panel's own chrome (header, chips, padding) is what its height has beyond the row's, and does not change
    // with the height: the need is that and the height the row's tiles can use at this width.
    const panel = tiles.closest<HTMLElement>(".mm-panel");
    if (panel && onHeightNeed) {
      onHeightNeed(
        panel.offsetHeight -
          tiles.clientHeight +
          shelfRowNeed(tiles.clientWidth),
      );
    }
  }, [shelfSize.width, shelfSize.height, onHeightNeed]);
  const [measured, setMeasured] = useState<MeasureText | null>(null);
  // The status line is measured in its own font, read before paint and again when the fonts arrive.
  useLayoutEffect(() => {
    const host = box.current;
    if (!host) return undefined;
    let live = true;
    const read = () => {
      if (live) setMeasured(() => canvasMeasure(statusFont(host)));
    };
    read();
    void document.fonts?.ready.then(read);
    return () => {
      live = false;
    };
  }, []);
  const countWords =
    picked === null && count !== undefined && !shelf.latest
      ? count
      : todayWords(shelf);
  const choose = (workflowId: number | null) => {
    setChosen(workflowId);
    saveShelfWorkflow(workflowId);
  };
  return (
    <Panel
      title="Just finished"
      className="mm-shelf-panel"
      controls={
        <ShelfFilter choices={choices} chosen={narrowedTo} onChoose={choose} />
      }
      description={countWords}
      to="/activity"
      toLabel="Activity"
    >
      <div className="mm-shelf" data-testid="just-finished-shelf">
        <div ref={shelfRef} className="mm-shelf__rows">
          {shelf.tiles.length === 0 ? (
            readError ? (
              <p
                className="mm-shelf__empty mm-status-text"
                data-status="broken"
                role="alert"
              >
                {loadErrorMessage(readError, "what has finished")}
              </p>
            ) : (
              <p className="mm-shelf__empty">
                {NOTHING_FINISHED_WORDS[filter]}
              </p>
            )
          ) : null}
          <ShelfTiles
            tiles={shelf.tiles}
            tilesShown={fit?.across ?? null}
            caption={fit?.caption ?? "compact"}
            tileWidth={fit?.width ?? null}
            measure={measureText ?? measured}
            boxRef={box}
            onOpen={onOpen}
          />
        </div>
        <p
          className="sr-only"
          role="status"
          aria-live="polite"
          data-testid="shelf-announcement"
        >
          {announcement}
        </p>
      </div>
    </Panel>
  );
}
