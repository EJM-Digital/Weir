import { describe, expect, it } from "vitest";

import {
  GENERIC_FAILURE_MESSAGE,
  NETWORK_UNREACHABLE_MESSAGE,
  SIGN_IN_ENDED_MESSAGE,
} from "./api-error-text";
import { ApiHttpError } from "./client";
import { errorMessage, loadErrorMessage } from "./error-message";

const PATH = "/api/v1/suite/settings";

describe("errorMessage", () => {
  it("shows a failed request's plain message", () => {
    expect(
      errorMessage(
        new ApiHttpError(PATH, 422, "Name is required."),
        "Could not save.",
      ),
    ).toBe("Name is required.");
  });

  it("shows the sentence Weir's own code threw", () => {
    expect(errorMessage(new Error("Disk full"), "Could not save.")).toBe(
      "Disk full",
    );
    expect(errorMessage({ message: "Refused" }, "Could not save.")).toBe(
      "Refused",
    );
    expect(errorMessage("Timed out", "Could not save.")).toBe("Timed out");
  });

  it("hides a browser fault behind the fallback", () => {
    expect(
      errorMessage(
        new TypeError("Cannot read properties of undefined (reading 'id')"),
        "Could not save.",
      ),
    ).toBe("Could not save.");
    expect(
      errorMessage(new SyntaxError("Unexpected token '<'"), "Could not save."),
    ).toBe("Could not save.");
  });

  it("falls back when there is nothing to show", () => {
    expect(errorMessage(new Error("   "), "Could not save.")).toBe(
      "Could not save.",
    );
    expect(errorMessage(null, "Could not save.")).toBe("Could not save.");
    expect(errorMessage(42, "Could not save.")).toBe("Could not save.");
  });

  it("has a plain default when the caller gives no fallback", () => {
    expect(errorMessage(null)).toBe(GENERIC_FAILURE_MESSAGE);
  });
});

describe("loadErrorMessage", () => {
  it("asks for a reload when the server answered with an error", () => {
    expect(
      loadErrorMessage(
        new ApiHttpError(PATH, 500, "Could not load settings."),
        "these settings",
      ),
    ).toBe("Weir couldn't load these settings. Reload the page to try again.");
  });

  it("says the server's own sentence when a load found the database busy", () => {
    const sentence =
      "Weir is busy with other changes and could not finish this request. Try again in 30 seconds.";

    expect(
      loadErrorMessage(
        new ApiHttpError(
          PATH,
          503,
          sentence,
          sentence,
          false,
          false,
          "database_busy",
        ),
        "these settings",
      ),
    ).toBe(sentence);
  });

  it("asks for a new sign-in when the session has ended", () => {
    expect(
      loadErrorMessage(
        new ApiHttpError(PATH, 401, SIGN_IN_ENDED_MESSAGE),
        "these settings",
      ),
    ).toBe(SIGN_IN_ENDED_MESSAGE);
  });

  it("says Weir can't be reached when no answer came back", () => {
    expect(
      loadErrorMessage(
        new ApiHttpError(
          PATH,
          0,
          NETWORK_UNREACHABLE_MESSAGE,
          undefined,
          false,
          true,
        ),
        "these settings",
      ),
    ).toBe(NETWORK_UNREACHABLE_MESSAGE);
  });
});
