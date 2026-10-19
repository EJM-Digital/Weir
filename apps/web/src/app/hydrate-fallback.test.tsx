import { render, screen, within } from "@testing-library/react";
import { expect, it } from "vitest";

import { AppHydrateFallback } from "./hydrate-fallback";

it("says Weir is loading, politely, inside a frame shaped like the app so nothing jumps when the page arrives", () => {
  render(<AppHydrateFallback />);

  const frame = screen.getByTestId("app-hydrate-fallback");
  const status = within(frame).getByRole("status");
  expect(status).toHaveTextContent("Loading Weir");
  expect(status).toHaveAttribute("aria-live", "polite");
  expect(within(frame).getByRole("main")).toContainElement(status);
});
