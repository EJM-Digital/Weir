import { useCallback, useState } from "react";
import type { KeyboardEvent, PointerEvent } from "react";

import { roundScaleTop } from "../../../lib/ui/chart-scale";
import { useCloseOnOutsideAndEscape } from "../../../lib/ui/use-close-on-outside";
import { useAppClockFormatter } from "../../../lib/ui/mm-format-date";
import { useElementSize } from "../../../lib/ui/use-element-size";
import type { HandedBack } from "../handed-back-model";
import {
  NOTHING_HANDED_BACK,
  bucketWords,
  scaleLabel,
  spanLabel,
  toneCounts,
} from "./handed-back-words";
import { TodayChartReadout } from "./today-chart-readout";
import { TodayLegend } from "./today-legend";
import {
  CHART_HEIGHT,
  CHART_WIDTH,
  chartShape,
  pointedAfterKey,
  minorLabelsFit,
  pointedAtFraction,
  scaleLines,
} from "./today-chart-model";

const GRADIENT_ID = "mm-today-fill";

type TodayChartProps = {
  handed: HandedBack;
  now: number;
};

/**
 * The last two hours as a filled line, five minutes to a point, on a round-number scale with its time
 * span inside its bottom edge. The pointer, a tap or the keyboard picks a point and a readout says what finished then.
 * It is a slider over the points, so the keyboard reaches each five minutes the pointer does, and a
 * screen reader reads that point's value text.
 */
export function TodayChart({ handed, now }: TodayChartProps) {
  const [pointed, setPointed] = useState<number | null>(null);
  const [chartRef, chartSize] = useElementSize<HTMLDivElement>();
  const clock = useAppClockFormatter();
  const hide = useCallback(() => setPointed(null), []);
  useCloseOnOutsideAndEscape(pointed !== null, hide, chartRef);

  const last = handed.buckets.length - 1;
  const current = pointed ?? last;
  const scaleTop = roundScaleTop(handed.peak, { wholeNumbers: true });
  const lines = scaleLines(scaleTop);
  const shape = chartShape(handed.buckets, scaleTop);
  const dot = shape.points[current];
  const span = spanLabel(handed, now);
  const showMinor = minorLabelsFit(lines, chartSize.height);

  const onPointerAt = (event: PointerEvent<HTMLElement>) => {
    const box = event.currentTarget.getBoundingClientRect();
    setPointed(pointedAtFraction((event.clientX - box.left) / box.width, last));
  };
  const onKeyDown = (event: KeyboardEvent) => {
    const next = pointedAfterKey(event.key, current, last);
    if (next === null) return;
    event.preventDefault();
    setPointed(next);
  };
  return (
    <div
      ref={chartRef}
      className="mm-today-chart"
      title={
        handed.totals.all > 0
          ? `Last 2 hours: ${toneCounts(handed.totals)}`
          : undefined
      }
      role="slider"
      tabIndex={0}
      aria-label="Files finished, five minutes to a point"
      aria-valuemin={0}
      aria-valuemax={last}
      aria-valuenow={current}
      aria-valuetext={bucketWords(handed.buckets[current], now)}
      onPointerDown={onPointerAt}
      onPointerMove={onPointerAt}
      onPointerLeave={(event) => {
        if (event.pointerType !== "touch") hide();
      }}
      onFocus={() => setPointed((already) => already ?? last)}
      onBlur={hide}
      onKeyDown={onKeyDown}
    >
      <svg
        aria-hidden="true"
        viewBox={`0 0 ${CHART_WIDTH} ${CHART_HEIGHT}`}
        preserveAspectRatio="none"
        className="mm-today-chart__svg"
      >
        <defs>
          <linearGradient id={GRADIENT_ID} x1="0" y1="0" x2="0" y2="1">
            <stop offset="0" className="mm-today-chart__stop" />
            <stop
              offset="1"
              className="mm-today-chart__stop mm-today-chart__stop--end"
            />
          </linearGradient>
        </defs>
        <path d={shape.area} fill={`url(#${GRADIENT_ID})`} />
        <path
          d={shape.line}
          className="mm-today-chart__line"
          fill="none"
          vectorEffect="non-scaling-stroke"
        />
      </svg>
      {handed.totals.all === 0 ? (
        <span aria-hidden="true" className="mm-today-chart__empty">
          {NOTHING_HANDED_BACK}
        </span>
      ) : null}
      {dot ? (
        <span
          aria-hidden="true"
          className="mm-today-chart__dot"
          style={{
            left: `${(dot.x / CHART_WIDTH) * 100}%`,
            top: `${(dot.y / CHART_HEIGHT) * 100}%`,
          }}
        />
      ) : /* istanbul ignore next -- @preserve */ null}
      <div
        aria-hidden="true"
        className={pointed === null ? "cs-layer" : "cs-layer cs-on"}
      >
        {lines.map((line) => {
          const style = { top: `${(line.y / CHART_HEIGHT) * 100}%` };
          return (
            <span key={line.value}>
              <i className="cs-grid" style={style} />
              {line.minor && !showMinor ? null : (
                <b className="cs-tick cs-below" style={style}>
                  {scaleLabel(line.value)}
                </b>
              )}
            </span>
          );
        })}
        <TodayLegend totals={handed.totals} />
        <span className="cs-end cs-now">{span}</span>
        {pointed !== null && dot ? (
          <TodayChartReadout
            bucket={handed.buckets[pointed]}
            fraction={dot.x / CHART_WIDTH}
            chartWidth={chartSize.width}
            clock={clock}
          />
        ) : null}
      </div>
    </div>
  );
}
