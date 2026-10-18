import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { afterEach, describe, expect, it } from "vitest";

import {
  UnsavedChangesScope,
  useUnsavedChanges,
  useUnsavedChangesRegistry,
} from "./unsaved-changes";

/** A panel that reports what it has unsaved. */
function Panel({ thing }: { thing: string | null }) {
  useUnsavedChanges(thing);
  return <p>Panel</p>;
}

/** The page around a panel: it holds the registry, and the buttons stand for the person editing and saving. */
function Page() {
  const { unsaved, register } = useUnsavedChangesRegistry();
  const [shown, setShown] = useState(true);
  const [thing, setThing] = useState<string | null>(null);
  return (
    <>
      <p data-testid="unsaved">{unsaved ?? "nothing"}</p>
      <button type="button" onClick={() => setThing("Movies")}>
        Edit
      </button>
      <button type="button" onClick={() => setThing(null)}>
        Save
      </button>
      <button type="button" onClick={() => setShown(false)}>
        Close the panel
      </button>
      <UnsavedChangesScope register={register}>
        {shown ? <Panel thing={thing} /> : null}
      </UnsavedChangesScope>
    </>
  );
}

const press = (name: string) =>
  fireEvent.click(screen.getByRole("button", { name }));

/** Closing or reloading the tab fires this on the window; a listener that cancels it makes the browser ask first. */
function closeTheTab(): Event {
  const event = new Event("beforeunload", { cancelable: true });
  window.dispatchEvent(event);
  return event;
}

afterEach(cleanup);

describe("closing or reloading the browser tab", () => {
  it("is held for the person to confirm while a panel has edits not yet saved", () => {
    render(<Page />);
    press("Edit");

    expect(screen.getByTestId("unsaved")).toHaveTextContent("Movies");
    expect(closeTheTab().defaultPrevented).toBe(true);
  });

  it("is not held when nothing is unsaved", () => {
    render(<Page />);

    expect(closeTheTab().defaultPrevented).toBe(false);
  });

  it("stops being held once the edits are saved, so the listener is removed", () => {
    render(<Page />);
    press("Edit");
    press("Save");

    expect(screen.getByTestId("unsaved")).toHaveTextContent("nothing");
    expect(closeTheTab().defaultPrevented).toBe(false);
  });

  it("stops being held when the panel with the edits goes away", () => {
    render(<Page />);
    press("Edit");
    press("Close the panel");

    expect(screen.getByTestId("unsaved")).toHaveTextContent("nothing");
    expect(closeTheTab().defaultPrevented).toBe(false);
  });
});
