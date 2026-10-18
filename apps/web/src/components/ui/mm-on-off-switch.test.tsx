import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { MmOnOffSwitch } from "./mm-on-off-switch";

function switchOf(size?: "page" | "row") {
  render(
    <MmOnOffSwitch
      id="s"
      label="Movies enabled"
      enabled
      disabled={false}
      onChange={() => undefined}
      layout="control"
      size={size}
    />,
  );
  return screen.getByRole("radiogroup", { name: "Movies enabled" });
}

describe("the On / Off switch", () => {
  it("is a page field's height unless it is told it stands on a table row", () => {
    expect(switchOf()).toHaveClass("h-(--mm-control-height-page)");
  });

  it("is a table row's height, with the row's buttons, when it stands on one", () => {
    const group = switchOf("row");

    expect(group).toHaveClass("h-(--mm-control-height-row)");
    expect(screen.getByRole("radio", { name: "On" })).toHaveClass("text-xs");
  });
});

describe("choosing On or Off", () => {
  function renderSwitch(
    over: Partial<Parameters<typeof MmOnOffSwitch>[0]> = {},
  ) {
    const onChange = vi.fn();
    render(
      <MmOnOffSwitch
        id="s"
        label="Movies enabled"
        enabled
        disabled={false}
        onChange={onChange}
        {...over}
      />,
    );
    return onChange;
  }

  it("marks the side that is chosen, and says so to a screen reader", () => {
    renderSwitch({ enabled: false });

    expect(screen.getByRole("radio", { name: "Off" })).toHaveAttribute(
      "aria-checked",
      "true",
    );
    expect(screen.getByRole("radio", { name: "On" })).toHaveAttribute(
      "aria-checked",
      "false",
    );
  });

  it("hands back true for On and false for Off", () => {
    const onChange = renderSwitch();

    fireEvent.click(screen.getByRole("radio", { name: "Off" }));
    fireEvent.click(screen.getByRole("radio", { name: "On" }));

    expect(onChange.mock.calls).toEqual([[false], [true]]);
  });

  it("changes nothing while it is disabled", () => {
    const onChange = renderSwitch({ disabled: true });

    const off = screen.getByRole("radio", { name: "Off" });
    expect(off).toBeDisabled();
    fireEvent.click(off);

    expect(onChange).not.toHaveBeenCalled();
  });

  it("is named by its label whether the label sits above it, beside it or only for a screen reader", () => {
    for (const layout of ["default", "inline", "control"] as const) {
      const { unmount } = render(
        <MmOnOffSwitch
          id="s"
          label="Movies enabled"
          enabled
          disabled={false}
          onChange={() => undefined}
          layout={layout}
        />,
      );
      expect(
        screen.getByRole("radiogroup", { name: "Movies enabled" }),
      ).toBeInTheDocument();
      unmount();
    }
  });
});
