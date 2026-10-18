import {
  useCallback,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent,
  type PointerEvent,
} from "react";

import { motionAllowed } from "../../lib/ui/motion-allowed";
import { useAppClockSecondsFormatter } from "../../lib/ui/mm-format-date";
import { windowIsResizing } from "../../lib/ui/resizing-class";
import { useAnimationFrame } from "../../lib/ui/use-animation-frame";
import {
  TRACE_HEIGHT,
  TRACE_WIDTH,
  fractionOfTime,
  headTime,
  highestIn,
  rateScaleTop,
  traceShape,
  windowEndingAt,
  type TraceSample,
  type TraceWindow,
} from "./live-trace-math";
import {
  READOUT_KEYS,
  readoutFor,
  sampleAfterKey,
  type Readout,
} from "./live-trace-readout";

/** One line of a trace: what it is called in the readout, its colour (a CSS colour or variable) and its samples. */
export type TraceSeries = {
  key: string;
  /** Named in the readout when the trace has more than one line: "read", "write". */
  label?: string;
  colour: string;
  samples: readonly TraceSample[];
};

type LiveTraceProps = {
  series: readonly TraceSeries[];
  /** How much time the trace spans, in ms. */
  windowMs: number;
  /** The top of a percentage's scale; null for a rate, whose top is a round number just above what is shown. */
  top: number | null;
  /** The least a rate's top is, in the rate's own units, so a quiet trace stays low. */
  floorTop?: number;
  /** Writes a value for the readout: "37%", "4.1 MB/s". Keep it the same function between renders. */
  format: (value: number) => string;
  /** What the trace shows, for a screen reader. */
  label: string;
};

/** The time of the newest sample of any line; null while there is none. */
function newestAt(series: readonly TraceSeries[]): number | null {
  const times = series.flatMap((line) => line.samples.at(-1)?.at ?? []);
  return times.length === 0 ? null : Math.max(...times);
}

/** Where, and when, the newest sample reached the screen: the head glides for the second after it. */
type Arrival = { newest: number | null; reachedAt: number };

/**
 * Lines drawn over the last stretch of time, with no scale: the figure beside it says the reading now, and pointing
 * at the trace says what it was then. A new sample arrives every second; the line is drawn again at every animation
 * frame with its head gliding from the previous sample to the newest, so it slides left continuously, and a dot
 * glows on the head. Nothing moves when motion is not wanted (reduced motion, a window being resized): the line
 * is then drawn at its newest sample each time one arrives.
 */
export function LiveTrace({
  series,
  windowMs,
  top,
  floorTop = 0,
  format,
  label,
}: LiveTraceProps) {
  const gradientId = useId();
  const clock = useAppClockSecondsFormatter();
  const lineRefs = useRef(new Map<string, SVGPathElement>());
  const areaRefs = useRef(new Map<string, SVGPathElement>());
  const dotRefs = useRef(new Map<string, HTMLSpanElement>());
  // A trace that opens on samples already there is settled: only a sample that arrives later glides in.
  const arrival = useRef<Arrival>({
    newest: newestAt(series),
    reachedAt: Number.NEGATIVE_INFINITY,
  });
  const frameWindow = useRef<TraceWindow | null>(null);
  const pointer = useRef<number | null>(null);
  // The sample a keyboard has chosen, by its time: it stays on that sample as the line slides on.
  const keyed = useRef<number | null>(null);
  const [readout, setReadout] = useState<Readout | null>(null);
  const hintId = useId();

  const draw = useCallback(
    (time: number) => {
      const newest = newestAt(series);
      if (newest !== arrival.current.newest) {
        arrival.current = { newest, reachedAt: time };
      }
      const still = !motionAllowed() || windowIsResizing();
      const lead = series.find((line) => line.samples.length > 0);
      const head = lead
        ? headTime(
            lead.samples,
            still ? Infinity : time - arrival.current.reachedAt,
          )
        : null;
      if (head === null) {
        frameWindow.current = null;
        dotRefs.current.forEach((dot) => (dot.style.opacity = "0"));
        lineRefs.current.forEach((path) => path.setAttribute("d", ""));
        areaRefs.current.forEach((path) => path.setAttribute("d", ""));
        return;
      }
      const window = windowEndingAt(head, windowMs);
      frameWindow.current = window;
      const scale =
        top ??
        rateScaleTop(
          Math.max(0, ...series.map((line) => highestIn(line.samples, window))),
          floorTop,
        );
      for (const line of series) {
        const shape = traceShape(line.samples, window, scale);
        lineRefs.current.get(line.key)?.setAttribute("d", shape.line);
        areaRefs.current.get(line.key)?.setAttribute("d", shape.area);
        const dot = dotRefs.current.get(line.key);
        const last = shape.points.at(-1);
        if (!dot) continue;
        dot.style.opacity = last ? "1" : "0";
        if (last) {
          dot.style.left = `${(last.x / TRACE_WIDTH) * 100}%`;
          dot.style.top = `${(last.y / TRACE_HEIGHT) * 100}%`;
        }
      }
      const following =
        pointer.current ??
        (keyed.current === null ? null : fractionOfTime(window, keyed.current));
      if (following !== null) {
        const next = readoutFor(series, window, following, format, clock);
        setReadout((current) =>
          current?.text === next?.text && current?.fraction === next?.fraction
            ? current
            : next,
        );
      }
    },
    [series, windowMs, top, floorTop, clock, format],
  );

  // Drawn again whenever what is drawn changes, so a still trace is never a frame behind.
  useLayoutEffect(() => {
    draw(performance.now());
  }, [draw]);
  useAnimationFrame((time) => {
    if (motionAllowed() && !windowIsResizing()) draw(time);
  });

  const point = (event: PointerEvent<HTMLElement>) => {
    keyed.current = null;
    const box = event.currentTarget.getBoundingClientRect();
    if (box.width <= 0) return;
    pointer.current = (event.clientX - box.left) / box.width;
    if (frameWindow.current) {
      setReadout(
        readoutFor(series, frameWindow.current, pointer.current, format, clock),
      );
    }
  };
  const leave = () => {
    pointer.current = null;
    if (keyed.current === null) setReadout(null);
  };
  const choose = (at: number | null) => {
    keyed.current = at;
    pointer.current = null;
    const window = frameWindow.current;
    const next =
      at === null || !window
        ? null
        : readoutFor(series, window, fractionOfTime(window, at), format, clock);
    setReadout(next);
  };
  const onKeyDown = (event: KeyboardEvent<HTMLElement>) => {
    if (event.key === "Escape") {
      choose(null);
      return;
    }
    const key = READOUT_KEYS.find((candidate) => candidate === event.key);
    if (!key) return;
    event.preventDefault();
    const lead = series.find((line) => line.samples.length > 0);
    const window = frameWindow.current;
    choose(
      lead && window
        ? sampleAfterKey(lead.samples, window, keyed.current, key)
        : null,
    );
  };

  const flip = readout !== null && readout.fraction > 0.5;
  return (
    <div
      className="mm-trace"
      role="slider"
      tabIndex={0}
      aria-label={label}
      aria-describedby={hintId}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={readout ? Math.round(readout.fraction * 100) : 100}
      aria-valuetext={readout?.text}
      onPointerMove={point}
      onPointerDown={point}
      onPointerLeave={leave}
      onKeyDown={onKeyDown}
      onBlur={() => choose(null)}
    >
      <span id={hintId} className="sr-only">
        Left and right arrow keys read earlier and later values.
      </span>
      <svg
        className="mm-trace__svg"
        viewBox={`0 0 ${TRACE_WIDTH} ${TRACE_HEIGHT}`}
        preserveAspectRatio="none"
        aria-hidden="true"
      >
        <defs>
          {series.map((line) => (
            <linearGradient
              key={line.key}
              id={`${gradientId}-${line.key}`}
              x1="0"
              y1="0"
              x2="0"
              y2="1"
            >
              <stop
                offset="0"
                className="mm-trace__stop"
                style={{ stopColor: line.colour }}
              />
              <stop
                offset="1"
                className="mm-trace__stop mm-trace__stop--end"
                style={{ stopColor: line.colour }}
              />
            </linearGradient>
          ))}
        </defs>
        {series.map((line) => (
          <g key={line.key}>
            <path
              ref={(path) => {
                if (path) areaRefs.current.set(line.key, path);
                else areaRefs.current.delete(line.key);
              }}
              fill={`url(#${gradientId}-${line.key})`}
            />
            <path
              ref={(path) => {
                if (path) lineRefs.current.set(line.key, path);
                else lineRefs.current.delete(line.key);
              }}
              className="mm-trace__line"
              style={{ stroke: line.colour }}
              fill="none"
              vectorEffect="non-scaling-stroke"
            />
          </g>
        ))}
      </svg>
      {series.map((line) => (
        <span
          key={line.key}
          ref={(dot) => {
            if (dot) dotRefs.current.set(line.key, dot);
            else dotRefs.current.delete(line.key);
          }}
          aria-hidden="true"
          className="mm-trace__dot"
          style={{ "--mm-trace-colour": line.colour } as CSSProperties}
        />
      ))}
      {readout ? (
        <>
          <i
            aria-hidden="true"
            className="mm-trace__cursor"
            style={{ left: `${readout.fraction * 100}%` }}
          />
          <div
            aria-hidden="true"
            className={
              flip ? "mm-trace__read mm-trace__read--left" : "mm-trace__read"
            }
            style={{ left: `${readout.fraction * 100}%` }}
          >
            {readout.text}
          </div>
        </>
      ) : null}
    </div>
  );
}
