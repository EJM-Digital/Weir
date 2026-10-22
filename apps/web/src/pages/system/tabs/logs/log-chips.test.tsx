import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { LogChips } from "./log-chips";

describe("the log's chips", () => {
  it("shows a count beside a chip that has one, and none beside one that does not", () => {
    render(
      <LogChips
        ariaLabel="Show"
        onToggle={() => undefined}
        chips={[
          { value: "error", label: "Errors", count: 1234, pressed: false },
          { value: "info", label: "Info", pressed: true },
        ]}
      />,
    );

    expect(screen.getByRole("button", { name: /Errors/ })).toHaveTextContent(
      "Errors 1,234",
    );
    const info = screen.getByRole("button", { name: "Info" });
    expect(info).toHaveTextContent(/^Info$/);
    expect(info).toHaveAttribute("aria-pressed", "true");
  });

  it("hands over the chip that was pressed", () => {
    const onToggle = vi.fn();
    render(
      <LogChips
        ariaLabel="Show"
        onToggle={onToggle}
        chips={[{ value: "warning", label: "Warnings", pressed: false }]}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Warnings" }));

    expect(onToggle).toHaveBeenCalledWith("warning");
  });
});
