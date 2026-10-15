import { render, screen } from "@testing-library/react";
import { expect, it } from "vitest";

import { AboutFooter } from "./about-footer";

it("links Weir's source to its GitHub repository, in a new tab that sends nothing back", () => {
  render(<AboutFooter />);

  const link = screen.getByTestId("about-source-code-link");
  expect(link).toHaveTextContent("Source on GitHub");
  expect(link).toHaveAttribute("href", "https://github.com/jampat000/Weir");
  expect(link).toHaveAttribute("target", "_blank");
  expect(link).toHaveAttribute("rel", "noreferrer");
});

it("links the licence to the LICENSE file of that repository, in a new tab", () => {
  render(<AboutFooter />);

  const link = screen.getByRole("link", { name: "AGPL-3.0-or-later" });
  expect(link).toHaveAttribute(
    "href",
    "https://github.com/jampat000/Weir/blob/main/LICENSE",
  );
  expect(link).toHaveAttribute("target", "_blank");
  expect(link).toHaveAttribute("rel", "noreferrer");
});
