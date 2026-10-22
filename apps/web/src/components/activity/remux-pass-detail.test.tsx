import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { RemuxPassDetail } from "./remux-pass-detail";

describe("RemuxPassDetail", () => {
  it("renders structured remux fields from JSON detail", () => {
    const detail = JSON.stringify({
      outcome: "live_output_written",
      ok: true,
      relative_media_path: "movies/a.mkv",
      inspected_source_path: "/data/movies/a.mkv",
      stream_counts: { video: 1, audio: 2, subtitle: 0 },
      plan_summary: "video copy indices: [0] | audio out: #1 eng",
      audio_before: "A before",
      audio_after: "A after",
      subs_before: "S before",
      subs_after: "S after",
      removed_audio: ["Director commentary", "Japanese stereo"],
      removed_subtitles: ["spa", "fre"],
      after_track_lines_meaning: "Planned only.",
      remux_required: true,
      ffmpeg_argv: ["/bin/ffmpeg", "-i", "a.mkv", "out.mkv"],
    });
    render(<RemuxPassDetail detail={detail} />);
    expect(
      screen.getByTestId("processing-remux-activity-detail"),
    ).toBeInTheDocument();
    expect(screen.getByText("Outcome")).toBeInTheDocument();
    expect(screen.getByText("File processed")).toBeInTheDocument();
    expect(
      screen.getByText("Show track and cleanup details"),
    ).toBeInTheDocument();
    expect(screen.getByText("/data/movies/a.mkv")).toBeInTheDocument();
    expect(screen.getByText("Audio in file")).toBeInTheDocument();
    expect(screen.getByText("A before")).toBeInTheDocument();
    expect(screen.getByText("Director commentary")).toBeInTheDocument();
    expect(screen.getByText("Japanese stereo")).toBeInTheDocument();
    expect(screen.getByText("spa")).toBeInTheDocument();
    expect(screen.queryByText("None removed")).not.toBeInTheDocument();
    expect(screen.getByText(/ffmpeg command line/i)).toBeInTheDocument();
  });

  it("labels a rules rejection Rejected, not as a failure", () => {
    const detail = JSON.stringify({
      outcome: "failed_before_execution",
      ok: false,
      rejected_without_manager: true,
      relative_media_path: "movies/a.mkv",
      reason: "Rejected: it has no audio tracks.",
    });
    render(<RemuxPassDetail detail={detail} />);
    expect(screen.getByText("Rejected")).toBeInTheDocument();
    expect(screen.queryByText("Could not check file")).not.toBeInTheDocument();
  });

  it("falls back to raw string when detail is not JSON", () => {
    render(<RemuxPassDetail detail="not-json" />);
    expect(
      screen.getByTestId("processing-remux-activity-detail-raw"),
    ).toHaveTextContent("not-json");
  });
});

describe("RemuxPassDetail with less in the record", () => {
  const show = (record: Record<string, unknown>) =>
    render(
      <RemuxPassDetail
        detail={JSON.stringify({
          outcome: "live_skipped_not_required",
          relative_media_path: "movies/a.mkv",
          ...record,
        })}
      />,
    );

  it("counts a kind of stream the record does not list as none", () => {
    show({ stream_counts: { video: 1 } });

    expect(
      screen.getByText("Video 1, audio 0, subtitles 0"),
    ).toBeInTheDocument();
  });

  it("says no changes were needed when the plan needed none", () => {
    show({ remux_required: false });

    expect(
      screen.getByText("Changes needed").nextElementSibling,
    ).toHaveTextContent("No");
  });

  it("says when the logged ffmpeg command line was cut short", () => {
    show({
      ffmpeg_argv: ["ffmpeg", "-i", "a.mkv"],
      ffmpeg_argv_truncated: true,
    });

    expect(
      screen.getByText(/ffmpeg command line \(truncated in log\)/),
    ).toBeInTheDocument();
  });
});
