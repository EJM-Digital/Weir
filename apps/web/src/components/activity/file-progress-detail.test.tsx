import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { FileProgressDetail } from "./file-progress-detail";

describe("FileProgressDetail", () => {
  it("renders a running pass in plain language", () => {
    const detail = JSON.stringify({
      status: "processing",
      relative_media_path: "movies/Caddyshack.mkv",
      percent: 42.4,
      eta_seconds: 71,
      elapsed_seconds: 50,
      processed_seconds: 1200,
      duration_seconds: 2800,
      speed: "16x",
      message: "Weir is writing the cleaned-up file.",
    });
    render(<FileProgressDetail detail={detail} />);
    expect(
      screen.getByTestId("processing-processing-progress-detail"),
    ).toBeInTheDocument();
    expect(screen.getByText("Processing")).toBeInTheDocument();
    expect(screen.getByText("42%")).toBeInTheDocument();
    expect(screen.getByRole("progressbar")).toHaveAttribute(
      "aria-valuenow",
      "42",
    );
    expect(screen.getByText(/About 1m 11s left/i)).toBeInTheDocument();
    expect(
      screen.getByText(/Processing speed 16x realtime/i),
    ).toBeInTheDocument();
  });

  it("uses finished styling and copy when the pass completes", () => {
    const detail = JSON.stringify({
      status: "finished",
      relative_media_path: "movies/Mickey 17.mkv",
      percent: 100,
      eta_seconds: 0,
      elapsed_seconds: 183,
      message: "Weir is writing the cleaned-up file.",
    });
    render(<FileProgressDetail detail={detail} />);
    const card = screen.getByTestId("processing-processing-progress-detail");

    expect(card).toHaveAttribute("data-status", "done");
    expect(screen.getAllByText("Finished")).toHaveLength(2);
    expect(
      screen.getByText("Weir finished processing this file."),
    ).toBeInTheDocument();
    expect(screen.getByText("100%")).toBeInTheDocument();
    expect(screen.queryByText(/About 0s left/i)).not.toBeInTheDocument();
  });

  it("uses failed styling when the pass stops", () => {
    const detail = JSON.stringify({
      status: "failed",
      relative_media_path: "movies/Broken.mkv",
      percent: 63,
      reason: "ffmpeg stopped unexpectedly.",
    });
    render(<FileProgressDetail detail={detail} />);

    expect(
      screen.getByTestId("processing-processing-progress-detail"),
    ).toHaveAttribute("data-status", "broken");
    expect(screen.getAllByText("Stopped")).toHaveLength(2);
    expect(
      screen.getByText("ffmpeg stopped unexpectedly."),
    ).toBeInTheDocument();
  });

  it("shows plain text as it came", () => {
    render(<FileProgressDetail detail="Queued behind two files" />);
    expect(screen.getByText("Queued behind two files")).toHaveClass(
      "mm-activity-processing__raw",
    );
  });

  it.each([
    {
      name: "nothing is known yet",
      detail: {},
      title: "Weir is processing this file.",
      percent: "Working",
      metric: "Estimating time left",
      valuenow: null,
      width: "8%",
    },
    {
      name: "the file is named only by where it was inspected",
      detail: { inspected_source_path: "D:\\Incoming\\Alpha.mkv" },
      title: "Weir is processing Alpha.mkv.",
      percent: "Working",
      metric: "Estimating time left",
      valuenow: null,
      width: "8%",
    },
    {
      name: "the percentage is not a number",
      detail: { percent: "half", relative_media_path: "a/B.mkv" },
      title: "Weir is processing B.mkv.",
      percent: "Working",
      metric: "Estimating time left",
      valuenow: null,
      width: "8%",
    },
    {
      name: "the percentage is above a whole",
      detail: { percent: 150, relative_media_path: "a/B.mkv" },
      title: "Weir is processing B.mkv.",
      percent: "100%",
      metric: "Estimating time left",
      valuenow: "100",
      width: "100%",
    },
    {
      name: "the percentage is below none",
      detail: { percent: -5, relative_media_path: "a/B.mkv" },
      title: "Weir is processing B.mkv.",
      percent: "0%",
      metric: "Estimating time left",
      valuenow: "0",
      width: "0%",
    },
    {
      name: "a failed pass gives no reason of its own",
      detail: { status: "failed", percent: 10 },
      title: "Weir could not finish this file.",
      percent: "10%",
      metric: "Stopped",
      valuenow: "10",
      width: "10%",
    },
    {
      name: "the final checks have no time left to give",
      detail: { status: "finishing", percent: 99 },
      title: "Weir is processing this file.",
      percent: "99%",
      metric: "Final checks",
      valuenow: "99",
      width: "99%",
    },
    {
      name: "the final checks know how long are left",
      detail: { status: "finishing", percent: 99, eta_seconds: 71 },
      title: "Weir is processing this file.",
      percent: "99%",
      metric: "About 1m 11s left",
      valuenow: "99",
      width: "99%",
    },
    {
      name: "the status is one Weir does not know",
      detail: { status: "queued", percent: 3 },
      title: "Weir is processing this file.",
      percent: "3%",
      metric: "Processing",
      valuenow: "3",
      width: "3%",
    },
  ])(
    "reads a pass where $name",
    ({ detail, title, percent, metric, valuenow, width }) => {
      const { container } = render(
        <FileProgressDetail detail={JSON.stringify(detail)} />,
      );
      const bar = screen.getByRole("progressbar");

      expect(screen.getByText(title)).toBeInTheDocument();
      expect(screen.getByText(percent)).toBeInTheDocument();
      expect(
        container.querySelector(".mm-activity-processing__metrics"),
      ).toHaveTextContent(metric);
      if (valuenow === null) expect(bar).not.toHaveAttribute("aria-valuenow");
      else expect(bar).toHaveAttribute("aria-valuenow", valuenow);
      expect(bar.firstElementChild).toHaveStyle({ width });
    },
  );

  it("shows only the facts it has: no running time, no speed, and no progress through the file without its length", () => {
    render(
      <FileProgressDetail
        detail={JSON.stringify({ status: "processing", processed_seconds: 60 })}
      />,
    );

    expect(screen.queryByText(/Running/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Processed/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Processing speed/)).not.toBeInTheDocument();
  });
});
