import {
  StatFitUnit,
  StatSide,
  StatTile,
} from "../../../components/panels/stat-tile";
import { loadErrorMessage } from "../../../lib/api/error-message";
import { formatBytes } from "../../../lib/format/bytes";
import type { Filter } from "../processing-filter";
import { useHandedBack } from "../use-handed-back";
import { handedBackSentence } from "./handed-back-words";
import { showNeedsPanel } from "./show-needs-panel";
import { TodayChart } from "./today-chart";
import { useNeedsYou } from "./use-needs-you";
import { useTodayFigures } from "./use-today-figures";

const PENDING = "…";
const FAILED = "–";
const FAILED_SIDE = "couldn't load";

type TodayTileProps = {
  filter: Filter;
  now: number;
  /** Narrows every figure to one workflow; every workflow when null or left out. */
  workflowId?: number | null;
};

/**
 * What Weir has done today: how many files it cleaned and how much space that saved, with the last two
 * hours drawn under it. The aside takes the person to the Needs you panel, when anything is in it.
 */
export function TodayTile({ filter, now, workflowId = null }: TodayTileProps) {
  const { figures, error } = useTodayFigures(workflowId, filter, now);
  const needsALook = useNeedsYou(workflowId, filter).files.length;
  const { handed, total, partial } = useHandedBack(filter, now, workflowId);
  return (
    <StatTile
      label="Today"
      aside={
        needsALook > 0 ? (
          <button type="button" onClick={showNeedsPanel}>
            {needsALook.toLocaleString()} need a look →
          </button>
        ) : undefined
      }
      figure={
        <>
          <span data-testid="live-done-today">
            {figures
              ? figures.cleaned.toLocaleString()
              : error
                ? FAILED
                : PENDING}
          </span>
          <StatFitUnit>cleaned</StatFitUnit>
          {!figures && error ? (
            <>
              <StatSide>
                <span aria-hidden="true">{FAILED_SIDE}</span>
              </StatSide>
              <span className="sr-only" role="alert">
                {loadErrorMessage(error, "today's figures")}
              </span>
            </>
          ) : null}
          {figures?.savedBytes != null ? (
            <StatSide>{formatBytes(figures.savedBytes)} saved</StatSide>
          ) : null}
        </>
      }
    >
      <div className="mm-today" data-testid="live-handed-back">
        <TodayChart handed={handed} now={now} />
        <span className="sr-only" data-testid="live-handed-back-sum">
          {handedBackSentence(handed, total, partial)}
        </span>
      </div>
    </StatTile>
  );
}
