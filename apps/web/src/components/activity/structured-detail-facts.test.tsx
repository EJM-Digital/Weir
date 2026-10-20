import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { StructuredDetailFacts } from "./structured-detail-facts";

const fact = (label: string) =>
  screen.getByText(label).closest("div")?.querySelector("dd")?.textContent;

describe("an event's detail as plain fields", () => {
  it("shows plain text as it is", () => {
    render(<StructuredDetailFacts detail="alice signed in." />);

    expect(screen.getByText("alice signed in.")).toBeInTheDocument();
    expect(screen.queryByRole("term")).not.toBeInTheDocument();
  });

  it.each([
    ["a yes", { ok: true }, "Ok", "Yes"],
    ["a no", { ok: false }, "Ok", "No"],
    ["a number", { files_found: 3 }, "Files found", "3"],
    [
      "a word",
      { relative_media_path: "A/B.mkv" },
      "Relative media path",
      "A/B.mkv",
    ],
    ["an empty list", { removed_audio: [] }, "Removed audio", "None"],
    ["a list", { removed_audio: ["eng", "fre"] }, "Removed audio", "eng, fre"],
    ["a list with gaps", { tags: ["a", null, ""] }, "Tags", "a, —, —"],
    [
      "a record",
      { sizes: { before: 5, kept: true } },
      "Sizes",
      "Before: 5; Kept: Yes",
    ],
    ["a record with a gap", { sizes: { after: null } }, "Sizes", "After: —"],
  ])("writes %s in words", (_what, detail, label, value) => {
    render(<StructuredDetailFacts detail={JSON.stringify(detail)} />);

    expect(fact(label)).toBe(value);
  });

  it("leaves out fields that hold nothing", () => {
    render(
      <StructuredDetailFacts
        detail={JSON.stringify({ kept: "yes", missing: null, blank: "" })}
      />,
    );

    expect(screen.getAllByRole("term").map((term) => term.textContent)).toEqual(
      ["Kept"],
    );
  });

  it("shows nothing for a record with no field that holds anything", () => {
    const { container } = render(
      <StructuredDetailFacts detail={JSON.stringify({ missing: null })} />,
    );

    expect(container).toBeEmptyDOMElement();
  });
});
