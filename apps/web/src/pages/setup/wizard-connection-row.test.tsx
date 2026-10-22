import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import {
  WizardConnectionRow,
  type ConnectionRowModel,
} from "./wizard-connection-row";

const BASE: ConnectionRowModel = {
  name: "Deluno",
  address: "http://localhost:5099",
  answering: true,
  detail: null,
  testing: false,
  testError: null,
};

function show(over: Partial<ConnectionRowModel>, onTest = vi.fn()) {
  render(
    <WizardConnectionRow
      connection={{ ...BASE, ...over }}
      onTest={onTest}
      onRemove={() => undefined}
      removeDisabled={false}
    />,
  );
  return onTest;
}

describe("a connection made during setup", () => {
  it("says it is connected", () => {
    show({});

    expect(screen.getByText("✓ Connected")).toBeInTheDocument();
  });

  it("says why it could not connect when the test gave a reason", () => {
    show({ answering: false, detail: "Connection refused." });

    expect(screen.getByRole("alert")).toHaveTextContent(
      "Weir could not connect to Deluno.",
    );
    expect(screen.getByText("Connection refused.")).toBeInTheDocument();
  });

  it("still says it could not connect when the test gave no reason", () => {
    show({ answering: false, detail: null });

    expect(screen.getByRole("alert")).toHaveTextContent(
      "Weir could not connect to Deluno.",
    );
    expect(screen.queryByText("Connection refused.")).toBeNull();
  });

  it("tests a connection nobody has tested as soon as it appears, once", () => {
    const onTest = show({ answering: null });

    expect(onTest).toHaveBeenCalledTimes(1);
    expect(screen.getByText(/Deluno/)).toBeInTheDocument();
  });

  it("says it is testing, and says when the test itself could not run", () => {
    show({ testing: true, answering: true, testError: "Could not test." });

    expect(screen.getAllByText("Testing…").length).toBeGreaterThan(0);
    expect(screen.getByText("Could not test.")).toBeInTheDocument();
  });

  it("lets the person test it again", () => {
    const onTest = show({});

    fireEvent.click(screen.getByRole("button", { name: /Test/ }));

    expect(onTest).toHaveBeenCalledTimes(1);
  });
});
