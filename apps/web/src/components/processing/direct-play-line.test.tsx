import { render, screen, within } from "@testing-library/react";
import { expect, it } from "vitest";

import type { ProcessingDirectPlay } from "../../lib/processing/files-api";
import { DirectPlayLine } from "./direct-play-line";

const LIVING_ROOM: ProcessingDirectPlay = {
  device_id: "tv",
  device_name: "Living room TV",
  verdict: "yes",
  reasons: [],
};
const PHONE: ProcessingDirectPlay = {
  device_id: "phone",
  device_name: "Phone",
  verdict: "no",
  reasons: ["cannot play DTS audio", "cannot play HEVC video"],
};
const TABLET: ProcessingDirectPlay = {
  device_id: "tablet",
  device_name: "Tablet",
  verdict: "maybe",
  reasons: ["may not play 10-bit video"],
};
const NEW_BOX: ProcessingDirectPlay = {
  device_id: "box",
  device_name: "New box",
  verdict: "unknown",
  reasons: [],
};

it("shows nothing for a file when no device was chosen", () => {
  const { container } = render(<DirectPlayLine directPlay={[]} />);

  expect(container).toBeEmptyDOMElement();
});

it("names each chosen device with its verdict in words as well as marks, and the first reason shortened", () => {
  render(
    <DirectPlayLine
      directPlay={[LIVING_ROOM, PHONE, TABLET, NEW_BOX]}
      testId="line"
    />,
  );

  const line = screen.getByTestId("line");
  expect(line).toHaveTextContent("Direct Play:");
  expect(line).toHaveTextContent("Living room TV ✓");
  expect(line).toHaveTextContent("Phone ✗");
  expect(line).toHaveTextContent("DTS audio, +1 more");
  expect(line).toHaveTextContent("Tablet ?");
  expect(line).toHaveTextContent("may not play 10-bit video");
  expect(line).toHaveTextContent("New box (not measured yet)");
  expect(within(line).getByText("yes")).toHaveClass("sr-only");
  expect(within(line).getByText("no")).toHaveClass("sr-only");
  expect(within(line).getByText("maybe")).toHaveClass("sr-only");
});

it("marks each device by what its verdict means", () => {
  const { container } = render(
    <DirectPlayLine directPlay={[LIVING_ROOM, PHONE, TABLET, NEW_BOX]} />,
  );

  const marked = [...container.querySelectorAll("[data-status]")].map(
    (device) => [
      device.textContent?.split(" ").slice(0, 2).join(" "),
      device.getAttribute("data-status"),
    ],
  );
  expect(marked).toEqual([
    ["Living room", "done"],
    ["Phone ✗", "broken"],
    ["Tablet ?", "attention"],
    ["New box", "idle"],
  ]);
});

it("explains in full, under Why, only the devices that have a reason, in the sentence a tooltip repeats", () => {
  render(<DirectPlayLine directPlay={[LIVING_ROOM, PHONE, NEW_BOX]} />);

  const why = screen.getByText("Why").closest("details")!;
  const sentences = within(why)
    .getAllByRole("listitem", { hidden: true })
    .map((item) => item.textContent);
  expect(sentences).toEqual([
    "Phone cannot play it directly, so the media server will convert it: cannot play DTS audio; cannot play HEVC video.",
  ]);
  expect(screen.getByTitle(sentences[0]!)).toBeInTheDocument();
});

it("leaves Why out when no device has a reason", () => {
  render(<DirectPlayLine directPlay={[LIVING_ROOM, NEW_BOX]} />);

  expect(screen.queryByText("Why")).toBeNull();
  expect(
    screen.getByTitle("New box: not measured yet, so Weir cannot say."),
  ).toBeInTheDocument();
});
