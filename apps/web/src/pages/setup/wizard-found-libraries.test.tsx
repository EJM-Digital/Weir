import { render } from "@testing-library/react";
import { expect, it, vi } from "vitest";

import { WizardFoundLibraries } from "./wizard-found-libraries";

it("shows nothing until a connection has answered", () => {
  const notAsked = {
    status: { isLoading: false, isError: false, data: undefined },
    rows: [],
    notes: [],
  } as unknown as Parameters<typeof WizardFoundLibraries>[0]["found"];

  const { container } = render(
    <WizardFoundLibraries
      found={notAsked}
      managers={[]}
      disabled={false}
      onNeither={vi.fn()}
    />,
  );

  expect(container).toBeEmptyDOMElement();
});
