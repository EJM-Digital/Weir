import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { RESIZING_CLASS } from "../../lib/ui/resizing-class";
import { LiveTrace, type TraceSeries } from "./live-trace";
import type { TraceSample } from "./live-trace-math";

vi.mock("../../lib/ui/mm-format-date", () => ({
  useAppClockSecondsFormatter: () => (ms: number) => `t${ms / 1000}`,
}));

const WINDOW_MS = 10_000;
const percent = (value: number) => `${Math.round(value)}%`;

const samples = (...values: number[]): TraceSample[] =>
  values.map((value, index) => ({ at: 20_000 + index * 1000, value }));

const series = (...values: number[]): TraceSeries[] => [
  { key: "cpu", colour: "var(--mm-success)", samples: samples(...values) },
];

function Trace({
  lines,
  windowMs = WINDOW_MS,
}: {
  lines: TraceSeries[];
  windowMs?: number;
}) {
  return (
    <LiveTrace
      series={lines}
      windowMs={windowMs}
      top={100}
      format={percent}
      label="CPU, last 10 minutes"
    />
  );
}

const line = () =>
  screen
    .getByRole("slider", { name: "CPU, last 10 minutes" })
    .querySelector<SVGPathElement>(".mm-trace__line")!;
const dot = () =>
  screen
    .getByRole("slider", { name: "CPU, last 10 minutes" })
    .querySelector<HTMLElement>(".mm-trace__dot")!;
const lastPoint = () => {
  const points = (line().getAttribute("d") ?? "").split(" ");
  return {
    x: Number(points[points.length - 2].replace("L", "")),
    y: Number(points[points.length - 1]),
  };
};
/** Where a value from 0 to 100 sits on the line, as the drawing's own units put it. */
const yOf = (value: number) => 3 + 45 * (1 - value / 100);

beforeEach(() => {
  vi.useFakeTimers({
    toFake: ["requestAnimationFrame", "cancelAnimationFrame", "performance"],
  });
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  document.body.classList.remove(RESIZING_CLASS);
});

describe("LiveTrace", () => {
  it("draws the samples as a line and says what it shows", () => {
    render(<Trace lines={series(10, 20, 30)} />);

    expect(
      screen.getByRole("slider", { name: "CPU, last 10 minutes" }),
    ).toBeInTheDocument();
    expect(line().getAttribute("d")).toMatch(/^M/);
  });

  it("draws nothing, and shows no dot, with no samples", () => {
    render(<Trace lines={series()} />);

    expect(line().getAttribute("d")).toBe("");
    expect(dot().style.opacity).toBe("0");
  });

  it("glows a dot on the newest point, at the right edge once it has arrived", () => {
    render(<Trace lines={series(10, 20, 30)} />);

    expect(dot().style.opacity).toBe("1");
    expect(dot().style.left).toBe("100%");
  });

  it("glides the head to a sample that arrives, rather than jumping to it", () => {
    const { rerender } = render(<Trace lines={series(10, 20, 30)} />);

    rerender(<Trace lines={series(10, 20, 30, 40)} />);
    expect(lastPoint().y).toBeCloseTo(yOf(30), 1);

    act(() => {
      vi.advanceTimersByTime(500);
    });
    expect(lastPoint().y).toBeLessThan(yOf(30));
    expect(lastPoint().y).toBeGreaterThan(yOf(40));

    act(() => {
      vi.advanceTimersByTime(600);
    });
    expect(lastPoint().y).toBeCloseTo(yOf(40), 1);
  });

  it("slides the whole line left as the head moves on", () => {
    const { rerender } = render(<Trace lines={series(10, 90, 10)} />);
    const settled = line().getAttribute("d");

    rerender(<Trace lines={series(10, 90, 10, 10)} />);
    act(() => {
      vi.advanceTimersByTime(500);
    });

    expect(line().getAttribute("d")).not.toBe(settled);
  });

  it("puts the head at the newest sample at once when reduced motion is asked for", () => {
    vi.stubGlobal("matchMedia", (query: string) => ({
      matches: query.includes("reduce"),
    }));
    const { rerender } = render(<Trace lines={series(10, 20, 30)} />);

    rerender(<Trace lines={series(10, 20, 30, 40)} />);

    expect(lastPoint().y).toBeCloseTo(yOf(40), 1);
  });

  it("holds still while the window is being resized", () => {
    document.body.classList.add(RESIZING_CLASS);
    const { rerender } = render(<Trace lines={series(10, 20, 30)} />);

    rerender(<Trace lines={series(10, 20, 30, 40)} />);

    expect(lastPoint().y).toBeCloseTo(yOf(40), 1);
  });

  it("does not glide the head while the window is being resized", () => {
    const { rerender } = render(<Trace lines={series(10, 20, 30)} />);
    rerender(<Trace lines={series(10, 20, 30, 40)} />);
    document.body.classList.add(RESIZING_CLASS);

    act(() => {
      vi.advanceTimersByTime(500);
    });

    expect(lastPoint().y).toBeCloseTo(yOf(30), 1);
  });

  it("shows no dot for a series with no samples while another has some", () => {
    render(
      <Trace
        lines={[
          { key: "read", label: "read", colour: "red", samples: samples(1, 2) },
          { key: "write", label: "write", colour: "blue", samples: [] },
        ]}
      />,
    );

    const dots = document.querySelectorAll<HTMLElement>(".mm-trace__dot");
    expect(dots[0].style.opacity).toBe("1");
    expect(dots[1].style.opacity).toBe("0");
  });

  it("draws one line for each series", () => {
    render(
      <Trace
        lines={[
          { key: "read", label: "read", colour: "red", samples: samples(1, 2) },
          {
            key: "write",
            label: "write",
            colour: "blue",
            samples: samples(3, 4),
          },
        ]}
      />,
    );

    expect(document.querySelectorAll(".mm-trace__line")).toHaveLength(2);
    expect(document.querySelectorAll(".mm-trace__dot")).toHaveLength(2);
  });
});

describe("LiveTrace's readout", () => {
  beforeEach(() => {
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockReturnValue({
      left: 0,
      top: 0,
      right: 100,
      bottom: 20,
      width: 100,
      height: 20,
      x: 0,
      y: 0,
      toJSON: () => ({}),
    });
  });

  it("says the time and the value under the pointer, in Weir's time zone", () => {
    render(<Trace lines={series(10, 20, 30, 40, 50)} />);

    fireEvent.pointerMove(
      screen.getByRole("slider", { name: "CPU, last 10 minutes" }),
      { clientX: 100 },
    );

    expect(screen.getByText("t24 · 50%")).toBeInTheDocument();
  });

  it("follows the pointer along the line", () => {
    render(<Trace lines={series(10, 20, 30, 40, 50)} />);

    fireEvent.pointerMove(
      screen.getByRole("slider", { name: "CPU, last 10 minutes" }),
      { clientX: 100 },
    );
    fireEvent.pointerMove(
      screen.getByRole("slider", { name: "CPU, last 10 minutes" }),
      { clientX: 80 },
    );

    expect(screen.getByText("t22 · 30%")).toBeInTheDocument();
  });

  it("reads out the new value under a pointer that stays still while a sample arrives", () => {
    const { rerender } = render(<Trace lines={series(10, 20, 30, 40, 50)} />);
    fireEvent.pointerMove(
      screen.getByRole("slider", { name: "CPU, last 10 minutes" }),
      { clientX: 100 },
    );
    expect(screen.getByText("t24 · 50%")).toBeInTheDocument();

    rerender(<Trace lines={series(10, 20, 30, 40, 50, 60)} />);
    act(() => {
      vi.advanceTimersByTime(1200);
    });

    expect(screen.queryByText("t24 · 50%")).toBeNull();
  });

  it("says nothing while the pointer is over a trace with no samples", () => {
    render(<Trace lines={series()} />);

    fireEvent.pointerMove(
      screen.getByRole("slider", { name: "CPU, last 10 minutes" }),
      { clientX: 50 },
    );

    expect(screen.queryByText(/^t\d+ · /)).toBeNull();
  });

  it("keeps the value picked with the keyboard when the pointer leaves", () => {
    render(<Trace lines={series(10, 20, 30)} />);
    const slider = screen.getByRole("slider", {
      name: "CPU, last 10 minutes",
    });

    fireEvent.keyDown(slider, { key: "ArrowLeft" });
    fireEvent.pointerLeave(slider);

    expect(slider.querySelector(".mm-trace__read")).not.toBeNull();
  });

  it("goes when the pointer leaves", () => {
    render(<Trace lines={series(10, 20, 30)} />);

    fireEvent.pointerMove(
      screen.getByRole("slider", { name: "CPU, last 10 minutes" }),
      { clientX: 100 },
    );
    fireEvent.pointerLeave(
      screen.getByRole("slider", { name: "CPU, last 10 minutes" }),
    );

    expect(screen.queryByText(/^t\d+ · /)).toBeNull();
  });

  it("sits on the side of the line the pointer is not on", () => {
    render(<Trace lines={series(10, 20, 30, 40, 50)} windowMs={4000} />);

    fireEvent.pointerMove(
      screen.getByRole("slider", { name: "CPU, last 10 minutes" }),
      { clientX: 100 },
    );
    expect(screen.getByText("t24 · 50%")).toHaveClass("mm-trace__read--left");

    fireEvent.pointerMove(
      screen.getByRole("slider", { name: "CPU, last 10 minutes" }),
      { clientX: 0 },
    );
    expect(screen.getByText("t20 · 10%")).not.toHaveClass(
      "mm-trace__read--left",
    );
  });
});

describe("LiveTrace's readout from the keyboard", () => {
  const trace = () =>
    screen.getByRole("slider", { name: "CPU, last 10 minutes" });
  const press = (key: string) => fireEvent.keyDown(trace(), { key });

  it("can be reached with Tab, and says how to read it", () => {
    render(<Trace lines={series(10, 20, 30)} />);

    expect(trace()).toHaveAttribute("tabindex", "0");
    expect(trace()).toHaveAccessibleDescription(
      "Left and right arrow keys read earlier and later values.",
    );
  });

  it("starts at the newest value on the first arrow, and goes earlier and later from there", () => {
    render(<Trace lines={series(10, 20, 30, 40, 50)} />);

    press("ArrowLeft");
    expect(trace()).toHaveAttribute("aria-valuetext", "t24 · 50%");
    press("ArrowLeft");
    expect(trace()).toHaveAttribute("aria-valuetext", "t23 · 40%");
    press("ArrowLeft");
    expect(trace()).toHaveAttribute("aria-valuetext", "t22 · 30%");
    press("ArrowRight");
    expect(trace()).toHaveAttribute("aria-valuetext", "t23 · 40%");
  });

  it("stops at the oldest and newest values, and Home and End go straight to them", () => {
    render(<Trace lines={series(10, 20, 30)} />);

    press("Home");
    expect(trace()).toHaveAttribute("aria-valuetext", "t20 · 10%");
    press("ArrowLeft");
    expect(trace()).toHaveAttribute("aria-valuetext", "t20 · 10%");
    press("End");
    expect(trace()).toHaveAttribute("aria-valuetext", "t22 · 30%");
    press("ArrowRight");
    expect(trace()).toHaveAttribute("aria-valuetext", "t22 · 30%");
  });

  it("shows the same card the pointer does, beside the value it says", () => {
    render(<Trace lines={series(10, 20, 30)} />);

    press("Home");

    expect(trace().querySelector(".mm-trace__read")?.textContent).toBe(
      "t20 · 10%",
    );
    expect(trace().querySelector(".mm-trace__cursor")).not.toBeNull();
  });

  it("goes on Escape and when the focus leaves, saying nothing more", () => {
    render(<Trace lines={series(10, 20, 30)} />);
    press("End");

    press("Escape");
    expect(trace()).not.toHaveAttribute("aria-valuetext");
    expect(trace().querySelector(".mm-trace__read")).toBeNull();

    press("End");
    fireEvent.blur(trace());
    expect(trace()).not.toHaveAttribute("aria-valuetext");
    expect(trace().querySelector(".mm-trace__read")).toBeNull();
  });

  it("leaves other keys alone", () => {
    render(<Trace lines={series(10, 20, 30)} />);

    const handled = fireEvent.keyDown(trace(), { key: "a" });

    expect(handled).toBe(true);
    expect(trace().querySelector(".mm-trace__read")).toBeNull();
  });

  it("says nothing for a trace with no samples", () => {
    render(<Trace lines={series()} />);

    press("ArrowLeft");

    expect(trace()).not.toHaveAttribute("aria-valuetext");
  });

  it("reads each line when there are two, as the pointer does", () => {
    const lines: TraceSeries[] = [
      {
        key: "read",
        label: "read",
        colour: "var(--mm-success)",
        samples: samples(1, 2, 3),
      },
      {
        key: "write",
        label: "write",
        colour: "var(--mm-primary)",
        samples: samples(4, 5, 6),
      },
    ];
    render(<Trace lines={lines} />);

    press("End");

    expect(trace()).toHaveAttribute(
      "aria-valuetext",
      "t22 · read 3% · write 6%",
    );
  });
});
