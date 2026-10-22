import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { LogFacts } from "./log-facts";

describe("the facts of an open log row", () => {
  it("shows nothing at all when none of them has anything to say", () => {
    const { container } = render(<LogFacts facts={[false, null, ""]} />);

    expect(container).toBeEmptyDOMElement();
  });
});
