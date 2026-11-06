import { describe, expect, it } from "vitest";

import {
  holdResolvesItself,
  holdWords,
  outOfScheduleWords,
} from "./reason-words";

describe("what an arriving file is waiting for", () => {
  it.each([
    "This file changed too recently. Weir waits 60s after the last change.",
    "This file is still growing, so something is writing to it.",
    "Weir has only just found this file and is checking whether anything is still writing to it.",
    "Weir is confirming that nothing is still writing to this file.",
    "This file stopped changing very recently. Weir waits 30s to be sure.",
    "Weir is waiting until no other program is writing this file.",
  ])("is waiting to settle: %s", (reason) => {
    expect(holdWords(reason)).toBe("Waiting to settle");
  });

  it("says a file Weir cannot open is one it cannot open yet", () => {
    expect(
      holdWords(
        "Weir could not open this file for reading — it is usually locked by whatever is still writing it.",
      ),
    ).toBe("Can't open it yet");
  });

  it("names an output folder Weir cannot write to, and a drive short of room", () => {
    expect(
      holdWords("Weir cannot write to the output folder (D:/Out), so ..."),
    ).toBe("Can't write output");
    expect(
      holdWords(
        "Waiting: the D: has less than 10.0 GB free (2.1 GB free now).",
      ),
    ).toBe("Waiting for space");
  });

  it("says a booked look is a look later", () => {
    expect(
      holdWords(
        "Weir has another look at this file booked, and leaves it alone until then.",
      ),
    ).toBe("Looking again later");
  });

  it("says only that a file is on hold when it does not recognise the reason", () => {
    expect(holdWords("Something nobody wrote a short word for.")).toBe(
      "On hold",
    );
  });
});

describe("which holds end by themselves", () => {
  it.each([
    "This file changed too recently. Weir waits 60s after the last change.",
    "Weir can't read this file yet. It may still be arriving, so Weir will look again later.",
    "Weir has another look at this file booked, and leaves it alone until then.",
  ])("is Weir's own wait: %s", (reason) => {
    expect(holdResolvesItself(reason)).toBe(true);
  });

  it.each([
    "Weir cannot write to the output folder (D:/Out), so ...",
    "Waiting: the D: has less than 10.0 GB free (2.1 GB free now).",
    "",
  ])("needs a person: %s", (reason) => {
    expect(holdResolvesItself(reason)).toBe(false);
  });
});

describe("why a file outside its schedule is not being worked on", () => {
  it("says Weir is paused, or that the workflow's hours are closed", () => {
    expect(
      outOfScheduleWords("Processing is paused. Weir will start work again."),
    ).toBe("Paused");
    expect(
      outOfScheduleWords(
        "The Movies workflow only runs inside its scheduled hours, and now is outside them.",
      ),
    ).toBe("Outside its hours");
  });

  it("says only that the file is on hold for any other reason", () => {
    expect(outOfScheduleWords("Because.")).toBe("On hold");
  });
});
