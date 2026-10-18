import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { libraryFile } from "../../test/library-fixtures";
import {
  renderLibraryPage,
  stubLibraryServer,
} from "../../test/library-server";
import type { StubbedApi } from "../../test/stub-weir-api";
import type { useTriggerLibraryScan } from "../../lib/processing/library-mode-queries";
import { LibraryCheckAgain } from "./library-scan-status";
import { LibrarySetupButton } from "./library-setup-button";

type Rescan = ReturnType<typeof useTriggerLibraryScan>;

const rescan = (state: Partial<Rescan> = {}) =>
  ({ isPending: false, mutate: vi.fn(), ...state }) as unknown as Rescan;

let api: StubbedApi | null = null;

afterEach(() => {
  if (api) expect(api.unmatched).toEqual([]);
  api = null;
  vi.unstubAllGlobals();
});

describe("Library setup in a header short of room", () => {
  it("is a mark alone, still named and tooltipped Library setup, and still opens the setup", () => {
    const onOpen = vi.fn();
    render(<LibrarySetupButton iconOnly onOpen={onOpen} />);

    const button = screen.getByRole("button", { name: "Library setup" });
    expect(button).toHaveAttribute("data-icon-only", "true");
    expect(button).toHaveAttribute("title", "Library setup");
    fireEvent.click(button);
    expect(onOpen).toHaveBeenCalledTimes(1);
  });

  it("keeps its words when there is room", () => {
    render(<LibrarySetupButton iconOnly={false} onOpen={vi.fn()} />);

    expect(
      screen.getByRole("button", { name: "Library setup" }),
    ).toHaveAttribute("data-icon-only", "false");
  });
});

describe("Check again in a header short of room", () => {
  it("is a mark alone, still named and tooltipped Check again, and still starts a check", () => {
    const start = rescan();
    render(<LibraryCheckAgain scan={null} rescan={start} iconOnly />);

    const button = screen.getByRole("button", { name: "Check again" });
    expect(button).toHaveAttribute("data-icon-only", "true");
    expect(button).toHaveAttribute("title", "Check again");
    fireEvent.click(button);
    expect(start.mutate).toHaveBeenCalledTimes(1);
  });

  it("keeps naming what it is doing while the mark stands alone", () => {
    render(
      <LibraryCheckAgain
        scan={null}
        rescan={rescan({ isPending: true })}
        iconOnly
      />,
    );

    const button = screen.getByRole("button", { name: "Starting a check…" });
    expect(button).toHaveAttribute("title", "Starting a check…");
    expect(button).toBeDisabled();
  });
});

describe("the Library page's header", () => {
  it("keeps the words of both buttons while the title line has room", async () => {
    api = stubLibraryServer([libraryFile()]);
    renderLibraryPage();

    expect(
      await screen.findByRole("button", { name: "Check again" }),
    ).toHaveAttribute("data-icon-only", "false");
    expect(
      await screen.findByRole("button", { name: "Library setup" }),
    ).toHaveAttribute("data-icon-only", "false");
  });
});
