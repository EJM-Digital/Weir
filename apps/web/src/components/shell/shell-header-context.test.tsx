import { render, renderHook, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import {
  ShellHeaderButtons,
  ShellHeaderProvider,
  ShellHeaderSlot,
  ShellHeaderTabs,
  useHeaderButtonsSlotRef,
  useHeaderSlotRef,
  useHeaderTabsSlotRef,
  usePageEyebrow,
  useSetPageEyebrow,
} from "./shell-header-context";

describe("the header's slots outside the shell", () => {
  it("leaves a page's controls, tabs and buttons where they are written", () => {
    render(
      <>
        <ShellHeaderSlot>
          <span>control</span>
        </ShellHeaderSlot>
        <ShellHeaderTabs>
          <span>tabs</span>
        </ShellHeaderTabs>
        <ShellHeaderButtons>
          <span>buttons</span>
        </ShellHeaderButtons>
      </>,
    );

    expect(screen.getByText("control")).toBeInTheDocument();
    expect(screen.getByText("tabs")).toBeInTheDocument();
    expect(screen.getByText("buttons")).toBeInTheDocument();
  });

  it("refuses to hand out a slot, since there is no header to hold it", () => {
    vi.spyOn(console, "error").mockImplementation(() => undefined);

    for (const useSlot of [
      useHeaderSlotRef,
      useHeaderTabsSlotRef,
      useHeaderButtonsSlotRef,
    ]) {
      expect(() => renderHook(useSlot)).toThrow(
        "The header slot belongs inside ShellHeaderProvider.",
      );
    }
  });

  it("sets no eyebrow, and reads none", () => {
    const { result } = renderHook(() => {
      useSetPageEyebrow("Processing");
      return usePageEyebrow();
    });

    expect(result.current).toBeNull();
  });
});

describe("the header's slots inside the shell", () => {
  it("shows nothing for a slot the header has not yet offered", () => {
    render(
      <ShellHeaderProvider>
        <ShellHeaderSlot>
          <span>control</span>
        </ShellHeaderSlot>
        <ShellHeaderTabs>
          <span>tabs</span>
        </ShellHeaderTabs>
        <ShellHeaderButtons>
          <span>buttons</span>
        </ShellHeaderButtons>
      </ShellHeaderProvider>,
    );

    expect(screen.queryByText("control")).toBeNull();
    expect(screen.queryByText("tabs")).toBeNull();
    expect(screen.queryByText("buttons")).toBeNull();
  });

  it("sets the eyebrow for as long as the page that set it is there, and takes it back after", () => {
    const wrapper = ShellHeaderProvider;
    const { result, rerender } = renderHook(
      ({ eyebrow }: { eyebrow: string | undefined }) => {
        useSetPageEyebrow(eyebrow);
        return usePageEyebrow();
      },
      {
        wrapper,
        initialProps: { eyebrow: "Processing" as string | undefined },
      },
    );
    expect(result.current).toBe("Processing");

    rerender({ eyebrow: undefined });
    expect(result.current).toBeNull();
  });
});
