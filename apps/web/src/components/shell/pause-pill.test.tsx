import { act, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { PauseState } from "../../lib/pause/pause-api";
import { canvasMeasure } from "../../lib/ui/measure-text";
import { PausePill } from "./pause-pill";

const SIX_PX_A_CHARACTER = 6;
const PILL_CHROME_PX = 30;

vi.mock("../../lib/ui/mm-format-date", async (importActual) => ({
  ...(await importActual<typeof import("../../lib/ui/mm-format-date")>()),
  useAppDateFormatter: () => () => "2 Oct, 10:00 pm",
  useAppClockFormatter: () => () => "10:00 pm",
}));
vi.mock("../../lib/ui/measure-text", async (importActual) => ({
  ...(await importActual<typeof import("../../lib/ui/measure-text")>()),
  canvasMeasure: vi.fn(
    () => (text: string) => text.length * SIX_PX_A_CHARACTER,
  ),
}));

const timed: PauseState = {
  paused: true,
  paused_until: "2026-10-02T22:00:00Z",
  scan_while_paused: true,
  reason: "Processing is paused. Weir will start work again at 10 pm.",
  in_flight_policy: "",
};

/** Lays out a header `width` px wide, and a pill that is its words' width and the pill's own chrome. */
function inHeaderOf(width: number, wrap: "nowrap" | "wrap" = "nowrap") {
  vi.spyOn(HTMLElement.prototype, "clientWidth", "get").mockImplementation(
    function (this: HTMLElement) {
      return this.classList.contains("mm-header") ? width : 0;
    },
  );
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(
    function (this: HTMLElement) {
      const words = this.textContent?.length ?? 0;
      return {
        width: words * SIX_PX_A_CHARACTER + PILL_CHROME_PX,
      } as DOMRect;
    },
  );
  return render(
    <header className="mm-header" style={{ flexWrap: wrap }}>
      <PausePill pause={timed} />
    </header>,
  );
}

afterEach(() => {
  vi.restoreAllMocks();
  Reflect.deleteProperty(document, "fonts");
});

describe("the pause pill's words", () => {
  it("says the fullest wording where the header has room for it", () => {
    inHeaderOf(1200);

    expect(screen.getByTestId("pause-badge")).toHaveTextContent(
      "Paused · until 2 Oct, 10:00 pm",
    );
  });

  it("says the time alone where the full date does not fit a fifth of the header", () => {
    // A fifth of 900 less the pill's 30px leaves 150px: the time alone is 138px, the full date 180px.
    inHeaderOf(900);

    expect(screen.getByTestId("pause-badge")).toHaveTextContent(
      "Paused · until 10:00 pm",
    );
  });

  it("says only that it is paused where nothing longer fits", () => {
    inHeaderOf(400);

    expect(screen.getByTestId("pause-badge")).toHaveTextContent(/^Paused$/);
  });

  it("lets the pill take half the header where the header wraps, as it has a row beside Pause", () => {
    inHeaderOf(500, "wrap");

    expect(screen.getByTestId("pause-badge")).toHaveTextContent(
      "Paused · until 2 Oct, 10:00 pm",
    );
  });

  it("keeps the server's own sentence for hovering whatever the wording", () => {
    inHeaderOf(400);

    expect(screen.getByTestId("pause-badge")).toHaveAttribute(
      "title",
      timed.reason,
    );
  });
});

describe("the pause pill as its pause changes", () => {
  it("takes the fullest words of the new pause when the old pause had an end and the new one has none", () => {
    const { rerender } = inHeaderOf(1200);

    rerender(
      <header className="mm-header" style={{ flexWrap: "nowrap" }}>
        <PausePill pause={{ ...timed, paused_until: null }} />
      </header>,
    );

    expect(screen.getByTestId("pause-badge")).toHaveTextContent(
      "Paused · until you resume",
    );
  });

  it("is not measured again by fonts that arrive after it has gone", async () => {
    let fontsArrive: () => void = () => undefined;
    Object.defineProperty(document, "fonts", {
      configurable: true,
      value: { ready: new Promise<void>((resolve) => (fontsArrive = resolve)) },
    });
    const { unmount } = inHeaderOf(1200);
    vi.mocked(canvasMeasure).mockClear();

    unmount();
    await act(async () => fontsArrive());

    expect(canvasMeasure).not.toHaveBeenCalled();
  });
});
