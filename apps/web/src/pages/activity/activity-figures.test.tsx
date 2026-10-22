import { render, screen } from "@testing-library/react";
import { expect, it } from "vitest";

import type { ProcessingFile } from "../../lib/processing/files-api";
import { SizeFigures, WorkingFigures } from "./activity-figures";
import { detailSizes } from "./activity-model";

const FILE = { status: "processed", size_bytes: 3826958687 } as const;

function figures(): Record<string, string> {
  const shown: Record<string, string> = {};
  for (const term of ["Before", "After", "Saved"]) {
    shown[term] = screen.getByText(term).nextElementSibling?.textContent ?? "";
  }
  return shown;
}

it("shows a small saving in its own unit, in the space-saved colour", () => {
  render(
    <SizeFigures
      sizes={detailSizes(FILE, {
        outcome: "live_output_written",
        pass_through_unchanged: false,
        source_size_bytes: 3826958687,
        output_size_bytes: 3826958687 - 412 * 1024,
      })}
    />,
  );
  expect(figures()).toEqual({
    Before: "3.56 GB",
    After: "3.56 GB",
    Saved: "412 KB",
  });
  expect(screen.getByText("412 KB")).toHaveClass("mm-payoff");
  expect(screen.queryByText(/handed the file back as it was/)).toBeNull();
});

it("says a file passed through unchanged was handed back as it was", () => {
  render(
    <SizeFigures
      sizes={detailSizes(FILE, {
        outcome: "live_output_written",
        pass_through_unchanged: true,
      })}
    />,
  );
  expect(figures()).toEqual({
    Before: "3.56 GB",
    After: "3.56 GB",
    Saved: "0 B",
  });
  expect(
    screen.getByText(/handed the file back as it was/),
  ).toBeInTheDocument();
});

it("says plainly when the sizes were not recorded, instead of showing equal numbers", () => {
  render(
    <SizeFigures
      sizes={detailSizes(FILE, {
        outcome: "live_output_written",
        pass_through_unchanged: false,
        source_size_bytes: null,
        output_size_bytes: null,
      })}
    />,
  );
  expect(figures()).toEqual({
    Before: "3.56 GB",
    After: "Not recorded",
    Saved: "Not recorded",
  });
  expect(screen.getByText(/^Size not recorded/)).toBeInTheDocument();
  expect(screen.queryByText(/handed the file back as it was/)).toBeNull();
});

it("shows an unknown size as a dash, and a pass the server has not measured as no progress", () => {
  const { unmount } = render(
    <SizeFigures
      sizes={detailSizes({ status: "processed", size_bytes: 0 }, null)}
    />,
  );
  expect(screen.getByText("Before").nextElementSibling).toHaveTextContent("—");
  unmount();

  render(
    <WorkingFigures
      file={{ status: "processing", progress_percent: null } as ProcessingFile}
    />,
  );
  expect(screen.getByRole("progressbar")).toHaveAttribute("aria-valuenow", "0");
  expect(screen.getByText("0%")).toBeInTheDocument();
});
