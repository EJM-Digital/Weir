// node --test scripts/check-npm-audit.test.mjs
import assert from "node:assert/strict";
import { test } from "node:test";

import { blockingAdvisories, judge } from "./check-npm-audit.mjs";

const advisory = {
  source: 1,
  name: "braces",
  severity: "high",
  title: "braces stack exhaustion",
  url: "https://github.com/advisories/GHSA-vfj7-8cjw-p6xm",
  range: "*",
};
const report = (fixAvailable) => ({
  vulnerabilities: {
    braces: { via: [advisory], fixAvailable },
    micromatch: { via: ["braces"], fixAvailable },
    "left-pad": {
      via: [
        {
          ...advisory,
          name: "left-pad",
          severity: "moderate",
          url: "https://github.com/advisories/GHSA-low",
        },
      ],
      fixAvailable: true,
    },
  },
});
const exception = {
  folder: "docs-site",
  advisory: "GHSA-vfj7-8cjw-p6xm",
  expires: "2026-11-14",
};

test("only high and critical advisories block, once each however many packages depend on them", () => {
  assert.deepEqual(
    blockingAdvisories(report(false)).map((a) => a.id),
    ["GHSA-vfj7-8cjw-p6xm"],
  );
});

test("an advisory with no exception blocks", () => {
  assert.equal(
    judge("docs-site", blockingAdvisories(report(false)), [], "2026-10-14")
      .length,
    1,
  );
});

test("an exception covers its own folder only", () => {
  assert.equal(
    judge(
      "apps/web",
      blockingAdvisories(report(false)),
      [exception],
      "2026-10-14",
    ).length,
    1,
  );
  assert.deepEqual(
    judge(
      "docs-site",
      blockingAdvisories(report(false)),
      [exception],
      "2026-10-14",
    ),
    [],
  );
});

test("an expired exception blocks again", () => {
  assert.match(
    judge(
      "docs-site",
      blockingAdvisories(report(false)),
      [exception],
      "2026-11-15",
    )[0],
    /expired/,
  );
});

test("an exception stops excusing an advisory once npm offers a fix", () => {
  assert.match(
    judge(
      "docs-site",
      blockingAdvisories(report(true)),
      [exception],
      "2026-10-14",
    )[0],
    /offers a fix/,
  );
});
