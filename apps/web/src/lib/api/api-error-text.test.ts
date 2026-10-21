import { describe, expect, it } from "vitest";

import {
  SIGN_IN_ENDED_MESSAGE,
  asSentence,
  codeOf,
  fieldLabel,
  responseErrorText,
  validationText,
} from "./api-error-text";

describe("fieldLabel", () => {
  it("turns code names into labels", () => {
    expect(fieldLabel("new_password")).toBe("New password");
    expect(fieldLabel("occurredUtc")).toBe("Occurred UTC");
    expect(fieldLabel("api_key")).toBe("API key");
    expect(fieldLabel("base_url")).toBe("Base URL");
  });
});

describe("asSentence", () => {
  it("adds a full stop only where one is missing", () => {
    expect(asSentence("Could not load settings")).toBe(
      "Could not load settings.",
    );
    expect(asSentence("Saved.")).toBe("Saved.");
  });
});

describe("validationText", () => {
  it("says a missing field is required", () => {
    expect(
      validationText([
        { loc: ["body", "display_name"], msg: "Field required" },
      ]),
    ).toBe("Display name is required.");
  });

  it("says an empty field can't be empty", () => {
    expect(
      validationText([
        {
          loc: ["body", "username"],
          msg: "String should have at least 1 character",
        },
      ]),
    ).toBe("Username can't be empty.");
  });

  it("gives the limits of a number", () => {
    expect(
      validationText([
        {
          loc: ["body", "max_files"],
          msg: "Input should be less than or equal to 8",
        },
        {
          loc: ["query", "limit"],
          msg: "Input should be greater than or equal to 1",
        },
      ]),
    ).toBe("Max files must be 8 or less. Limit must be 1 or more.");
  });

  it("passes a model's own check through as the sentence it already is", () => {
    expect(
      validationText([
        {
          loc: ["body"],
          msg: "Value error, Created after must be earlier than created before.",
        },
      ]),
    ).toBe("Created after must be earlier than created before.");
  });

  it("names the field without the server's wording for anything else", () => {
    expect(
      validationText([
        {
          loc: ["body", "rule_set_id"],
          msg: "Input should be a valid UUID, invalid length",
        },
      ]),
    ).toBe("Rule set ID isn't valid.");
  });

  it("says each problem once", () => {
    const issue = { loc: ["body", "name"], msg: "Field required" };
    expect(validationText([issue, issue])).toBe("Name is required.");
  });

  it("asks for a check when no field is named", () => {
    expect(
      validationText([
        { loc: ["body"], msg: "Input should be a valid dictionary" },
      ]),
    ).toBe("Weir couldn't accept that. Check the details and try again.");
  });
});

const BUSY_BODY = {
  detail:
    "Weir is busy with other changes and could not finish this request. Try again in 30 seconds.",
  code: "database_busy",
};

describe("responseErrorText", () => {
  it("shows the server's own sentence when the database is busy, whatever was being done", () => {
    for (const path of [
      "/api/v1/suite/settings",
      "/api/v1/processing/files",
      "/api/v1/auth/login",
    ]) {
      expect(
        responseErrorText({
          path,
          status: 503,
          body: BUSY_BODY,
          fallback: "Could not save",
        }),
      ).toBe(BUSY_BODY.detail);
    }
  });

  it("keeps the fallback for a 503 that is not a busy database, whose text is for the log", () => {
    expect(
      responseErrorText({
        path: "/api/v1/suite/settings",
        status: 503,
        body: { detail: "SQLITE_ERROR near line 3", code: "internal_error" },
        fallback: "Could not save",
      }),
    ).toBe("Could not save.");
  });

  it("keeps the sign-in form's own reason for a 401", () => {
    expect(
      responseErrorText({
        path: "/api/v1/auth/login",
        status: 401,
        body: { detail: "Invalid username or password." },
        fallback: "Sign-in failed",
      }),
    ).toBe("Invalid username or password.");
  });

  it("says the sign-in has ended for any other 401", () => {
    expect(
      responseErrorText({
        path: "/api/v1/processing/files",
        status: 401,
        body: { detail: "Not authenticated." },
        fallback: "Could not load files",
      }),
    ).toBe(SIGN_IN_ENDED_MESSAGE);
  });

  it("uses the fallback when there is no body at all", () => {
    expect(
      responseErrorText({
        path: "/api/v1/suite/settings",
        status: 404,
        body: undefined,
        fallback: "Could not load settings",
      }),
    ).toBe("Could not load settings.");
  });
});

describe("validationText for every wording the server uses", () => {
  it.each([
    [
      "String should have at least 3 characters",
      "Name must be at least 3 characters.",
    ],
    [
      "String should have at least 2 character",
      "Name must be at least 2 character.",
    ],
    [
      "String should have at most 40 characters",
      "Name must be at most 40 characters.",
    ],
    [
      "Input should be a valid integer, unable to parse string",
      "Name must be a whole number.",
    ],
    ["Input should be a finite number", "Name must be a number."],
    ["Input should be a valid number", "Name must be a number."],
    [
      "List should have at least 1 item after validation, not 0",
      "Name needs at least one entry.",
    ],
    ["Something nobody has seen", "Name isn't valid."],
  ])("says %j as %j", (msg, words) => {
    expect(validationText([{ loc: ["body", "name"], msg }])).toBe(words);
  });

  it.each([
    ["no problems at all", []],
    ["a problem that is not a record", ["nope", null]],
    ["a problem with no place", [{ msg: "Field required" }]],
    ["a place of the wrong kind", [{ loc: "name", msg: "Field required" }]],
    [
      "a place with only the body in it",
      [{ loc: ["body", 0], msg: "Field required" }],
    ],
  ])("asks for a check on %s", (_name, issues) => {
    expect(validationText(issues)).toBe(
      "Weir couldn't accept that. Check the details and try again.",
    );
  });

  it("names the last place in the path to the field, skipping list positions", () => {
    expect(
      validationText([
        { loc: ["body", "windows", 2, "start_time"], msg: "Field required" },
      ]),
    ).toBe("Start time is required.");
  });

  it("says a message with no text as the field not being valid", () => {
    expect(validationText([{ loc: ["body", "name"] }])).toBe(
      "Name isn't valid.",
    );
  });
});

describe("codeOf", () => {
  it.each([
    ["a code", { code: "database_busy" }, "database_busy"],
    ["a code that is not text", { code: 5 }, undefined],
    ["no code", { detail: "x" }, undefined],
    ["no record", "words", undefined],
    ["nothing", null, undefined],
  ])("reads %s", (_name, body, code) => {
    expect(codeOf(body)).toBe(code);
  });
});

describe("responseErrorText for the answers a person is shown", () => {
  const say = (status: number, body: unknown, path = "/api/v1/things") =>
    responseErrorText({ path, status, body, fallback: "Could not do it" });

  it.each([
    ["a refusal", 400, { detail: "Pick a folder" }, "Pick a folder."],
    [
      "a media manager's failure",
      502,
      { detail: "Sonarr said no." },
      "Sonarr said no.",
    ],
    [
      "a failure of Weir's own",
      500,
      { detail: "Traceback (most recent call last)" },
      "Could not do it.",
    ],
    ["a detail with only spaces", 400, { detail: "   " }, "Could not do it."],
    [
      "a detail that is not text",
      400,
      { detail: { a: 1 } },
      "Could not do it.",
    ],
    ["a body that is not a record", 400, "words", "Could not do it."],
    ["no body", 400, undefined, "Could not do it."],
    [
      "a list of problems",
      422,
      { detail: [{ loc: ["body", "name"], msg: "Field required" }] },
      "Name is required.",
    ],
  ])("shows the right words for %s", (_name, status, body, words) => {
    expect(say(status, body)).toBe(words);
  });

  it("says there have been too many attempts", () => {
    expect(say(429, { detail: "slow down" })).not.toBe("slow down.");
    expect(say(429, {})).toMatch(/too many/i);
  });
});
