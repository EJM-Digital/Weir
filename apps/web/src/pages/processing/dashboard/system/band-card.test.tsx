import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { BandCard } from "./band-card";

describe("a band card", () => {
  it("shows its aside only when it has one", () => {
    const { container, rerender } = render(
      <BandCard label="Storage" aside="3 drives">
        <p>Body</p>
      </BandCard>,
    );
    expect(screen.getByText("3 drives")).toBeInTheDocument();

    rerender(
      <BandCard label="Storage">
        <p>Body</p>
      </BandCard>,
    );
    expect(container.querySelector(".mm-stat__aside")).toBeNull();
  });
});
