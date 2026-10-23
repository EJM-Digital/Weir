import { render, screen } from "@testing-library/react";
import { expect, it } from "vitest";

import { RemuxPassDetail } from "./remux-pass-detail";

it("reads a kind the probe did not count as none", () => {
  render(
    <RemuxPassDetail
      detail={JSON.stringify({
        outcome: "live_output_written",
        ok: true,
        stream_counts: {},
      })}
    />,
  );

  expect(screen.getByText("Video 0, audio 0, subtitles 0")).toBeInTheDocument();
});
