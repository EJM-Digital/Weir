import { describe, expect, it } from "vitest";

import { EMPTY_RULE_SET } from "../../pages/settings/tabs/rules/rule-set-model";
import { recordedBody } from "../../test/recorded";
import {
  writeFromProcessingRuleSet,
  type ProcessingRuleSet,
  type ProcessingRuleSetWrite,
} from "./rule-sets-api";

/** Every field a person can set on a profile, at a value that differs from the one a new profile starts with. */
function everyFieldChanged(): ProcessingRuleSetWrite {
  const changed: Record<string, unknown> = {};
  for (const [key, started] of Object.entries(EMPTY_RULE_SET)) {
    if (typeof started === "boolean") changed[key] = !started;
    else if (typeof started === "number") changed[key] = started + 7;
    else if (typeof started === "string") changed[key] = `changed ${key}`;
    else
      changed[key] = Object.fromEntries(
        Object.keys(started).map((name) => [name, `changed ${key} ${name}`]),
      );
  }
  return changed as unknown as ProcessingRuleSetWrite;
}

describe("writeFromProcessingRuleSet", () => {
  it("sends back every rule a profile has, so saving one never resets another", () => {
    const edited = everyFieldChanged();
    const stored: ProcessingRuleSet = {
      ...edited,
      id: 4,
      used_by_library_count: 2,
      updated_at: "2026-07-28T04:00:00Z",
    };

    const write = writeFromProcessingRuleSet(stored);

    expect(write).toEqual(edited);
    for (const key of Object.keys(EMPTY_RULE_SET)) {
      expect(
        write[key as keyof ProcessingRuleSetWrite],
        `${key} is as stored`,
      ).not.toEqual(EMPTY_RULE_SET[key as keyof ProcessingRuleSetWrite]);
    }
  });

  it("leaves out what only the server works out", () => {
    const [recorded] = recordedBody<ProcessingRuleSet[]>(
      "GET /api/v1/processing/rule-sets",
    );

    const write = writeFromProcessingRuleSet(recorded!);

    expect(Object.keys(write).sort()).toEqual(
      Object.keys(EMPTY_RULE_SET).sort(),
    );
    expect(write).not.toHaveProperty("id");
    expect(write).not.toHaveProperty("used_by_library_count");
    expect(write).not.toHaveProperty("updated_at");
  });

  it("copies the track name overrides, so editing the copy leaves the stored profile alone", () => {
    const stored = { ...everyFieldChanged(), id: 1 } as ProcessingRuleSet;

    const write = writeFromProcessingRuleSet(stored);
    write.track_name_overrides.forced = "edited";

    expect(stored.track_name_overrides.forced).toBe(
      "changed track_name_overrides forced",
    );
  });
});
