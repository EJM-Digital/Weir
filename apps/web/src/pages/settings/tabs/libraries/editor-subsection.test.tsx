import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { EditorSubsection } from "./editor-subsection";

describe("an editor subsection", () => {
  it("shows its line only when it has one", () => {
    const { container, rerender } = render(
      <EditorSubsection title="Link" detail="What this is.">
        <p>Body</p>
      </EditorSubsection>,
    );
    expect(screen.getByText("What this is.")).toBeInTheDocument();

    rerender(
      <EditorSubsection title="Link">
        <p>Body</p>
      </EditorSubsection>,
    );
    expect(container.querySelector(".mm-editor-sub__detail")).toBeNull();
  });
});
