import { fireEvent, render, screen } from "@testing-library/react";
import { expect, it, vi } from "vitest";

import { ScheduleGridEditor } from "./schedule-grid-editor";

const SLOTS_PER_WEEK = 7 * 24 * 4;

it("treats an empty grid as no restriction rather than as never", () => {
  render(<ScheduleGridEditor value="" onChange={vi.fn()} />);

  expect(screen.getByTestId("schedule-grid")).toHaveTextContent(
    /runs at any time/i,
  );
});

it("writes all four quarters of an hour, so nothing is lost in the round trip", () => {
  const onChange = vi.fn();
  render(
    <ScheduleGridEditor
      value={"0".repeat(SLOTS_PER_WEEK)}
      onChange={onChange}
    />,
  );

  // Wednesday (day 2) at 14:00.
  fireEvent.pointerDown(screen.getByTestId("schedule-cell-2-14"));

  const written: string = onChange.mock.calls[0][0];
  const start = 2 * 96 + 14 * 4;
  expect(written.slice(start, start + 4)).toBe("1111");
  expect(written).toHaveLength(SLOTS_PER_WEEK);
});

it("turns an hour back off", () => {
  const onChange = vi.fn();
  render(
    <ScheduleGridEditor
      value={"1".repeat(SLOTS_PER_WEEK)}
      onChange={onChange}
    />,
  );

  fireEvent.pointerDown(screen.getByTestId("schedule-cell-0-9"));

  const written: string = onChange.mock.calls[0][0];
  expect(written.slice(9 * 4, 9 * 4 + 4)).toBe("0000");
});

it("clears back to an empty grid rather than an all-zero one", () => {
  const onChange = vi.fn();
  render(
    <ScheduleGridEditor
      value={"0".repeat(SLOTS_PER_WEEK)}
      onChange={onChange}
    />,
  );

  fireEvent.click(screen.getByTestId("schedule-clear"));

  // "" means any time; "000…" would mean never, and confusing the two would stop all work.
  expect(onChange).toHaveBeenCalledWith("");
});

it("can select nothing at all, which is a different thing from no schedule", () => {
  const onChange = vi.fn();
  render(<ScheduleGridEditor value="" onChange={onChange} />);

  fireEvent.click(screen.getByTestId("schedule-none"));

  expect(onChange).toHaveBeenCalledWith("0".repeat(SLOTS_PER_WEEK));
});

it("draws a malformed grid as unrestricted instead of inventing a schedule", () => {
  render(<ScheduleGridEditor value="not-a-grid" onChange={vi.fn()} />);

  expect(screen.getByTestId("schedule-cell-3-12")).toHaveAttribute(
    "aria-pressed",
    "true",
  );
});

it("names every hour by its day, time and state", () => {
  render(
    <ScheduleGridEditor
      value={"0".repeat(SLOTS_PER_WEEK)}
      onChange={vi.fn()}
    />,
  );

  expect(
    screen.getByRole("button", { name: "Monday 09:00, off" }),
  ).toBeInTheDocument();
  expect(
    screen.getByRole("button", { name: "Sunday 23:00, off" }),
  ).toBeInTheDocument();
});

it("puts one hour in the tab order, so Tab reaches the grid once", () => {
  render(<ScheduleGridEditor value="" onChange={vi.fn()} />);

  const reachable = screen
    .getAllByRole("button")
    .filter((button) => button.dataset.testid?.startsWith("schedule-cell-"))
    .filter((button) => button.tabIndex === 0);
  expect(reachable).toHaveLength(1);
});

it("moves between hours and days with the arrow keys", () => {
  render(<ScheduleGridEditor value="" onChange={vi.fn()} />);
  const start = screen.getByTestId("schedule-cell-0-0");
  start.focus();

  fireEvent.keyDown(start, { key: "ArrowRight" });
  expect(screen.getByTestId("schedule-cell-0-1")).toHaveFocus();

  fireEvent.keyDown(screen.getByTestId("schedule-cell-0-1"), {
    key: "ArrowDown",
  });
  expect(screen.getByTestId("schedule-cell-1-1")).toHaveFocus();
  expect(screen.getByTestId("schedule-cell-1-1").tabIndex).toBe(0);
  expect(screen.getByTestId("schedule-cell-0-0").tabIndex).toBe(-1);
});

it("stops at the edges of the week instead of wrapping", () => {
  render(<ScheduleGridEditor value="" onChange={vi.fn()} />);
  const start = screen.getByTestId("schedule-cell-0-0");
  start.focus();

  fireEvent.keyDown(start, { key: "ArrowUp" });
  fireEvent.keyDown(start, { key: "ArrowLeft" });

  expect(start).toHaveFocus();
});

it("switches the focused hour with Space and with Enter", () => {
  const onChange = vi.fn();
  render(
    <ScheduleGridEditor
      value={"0".repeat(SLOTS_PER_WEEK)}
      onChange={onChange}
    />,
  );
  const monday9 = screen.getByTestId("schedule-cell-0-9");

  fireEvent.keyDown(monday9, { key: " " });
  fireEvent.keyDown(monday9, { key: "Enter" });

  expect(onChange).toHaveBeenCalledTimes(2);
  const written: string = onChange.mock.calls[0][0];
  expect(written.slice(9 * 4, 9 * 4 + 4)).toBe("1111");
});

it("paints every hour a pointer drags across", () => {
  const onChange = vi.fn();
  render(
    <ScheduleGridEditor
      value={"0".repeat(SLOTS_PER_WEEK)}
      onChange={onChange}
    />,
  );

  fireEvent.pointerDown(screen.getByTestId("schedule-cell-0-9"));
  fireEvent.pointerEnter(screen.getByTestId("schedule-cell-0-10"));

  expect(onChange).toHaveBeenCalledTimes(2);
  const second: string = onChange.mock.calls[1][0];
  expect(second.slice(10 * 4, 10 * 4 + 4)).toBe("1111");
});

it("does not let a viewer change the grid", () => {
  const onChange = vi.fn();
  render(<ScheduleGridEditor value="" onChange={onChange} disabled />);

  fireEvent.pointerDown(screen.getByTestId("schedule-cell-1-8"));

  expect(onChange).not.toHaveBeenCalled();
});

it("jumps to the first and last hour of the day with Home and End", () => {
  render(<ScheduleGridEditor value="" onChange={vi.fn()} />);
  const noon = screen.getByTestId("schedule-cell-2-12");
  noon.focus();

  fireEvent.keyDown(noon, { key: "End" });
  expect(screen.getByTestId("schedule-cell-2-23")).toHaveFocus();

  fireEvent.keyDown(screen.getByTestId("schedule-cell-2-23"), { key: "Home" });
  expect(screen.getByTestId("schedule-cell-2-0")).toHaveFocus();
});

it("leaves the focus and the grid alone for a key that is not for moving or switching", () => {
  const onChange = vi.fn();
  render(<ScheduleGridEditor value="" onChange={onChange} />);
  const start = screen.getByTestId("schedule-cell-0-0");
  start.focus();

  fireEvent.keyDown(start, { key: "x" });

  expect(start).toHaveFocus();
  expect(onChange).not.toHaveBeenCalled();
});

it("paints nothing when a pointer passes over hours without being pressed, or after it is let go", () => {
  const onChange = vi.fn();
  render(
    <ScheduleGridEditor
      value={"0".repeat(SLOTS_PER_WEEK)}
      onChange={onChange}
    />,
  );

  fireEvent.pointerEnter(screen.getByTestId("schedule-cell-0-9"));
  expect(onChange).not.toHaveBeenCalled();

  fireEvent.pointerDown(screen.getByTestId("schedule-cell-0-9"));
  fireEvent.pointerUp(screen.getByRole("group", { name: "Hours in the week" }));
  onChange.mockClear();
  fireEvent.pointerEnter(screen.getByTestId("schedule-cell-0-10"));
  expect(onChange).not.toHaveBeenCalled();
});
