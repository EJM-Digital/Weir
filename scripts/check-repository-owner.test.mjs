// node --test scripts/check-repository-owner.test.mjs
import assert from "node:assert/strict";
import { test } from "node:test";

import { FORMER_OWNERS, PLACES, findProblems } from "./check-repository-owner.mjs";
import { REPOSITORY } from "./repository.mjs";

const REPO = "new-org/Weir";

const holding = (repository) => ({
  "apps/server/src/Weir.Core/Updates/ProjectLocation.cs": `public const string Owner = "${repository.split("/")[0]}";\n public const string Repo = "${repository.split("/")[1]}";`,
  "apps/tray/Weir.Tray/ProjectLocation.cs": `internal const string Owner = "${repository.split("/")[0]}";\n internal const string Repo = "${repository.split("/")[1]}";`,
  "apps/web/src/lib/repository.ts": `const REPOSITORY_SLUG = "${repository}";`,
  "scripts/repository.ps1": `$WeirRepository = '${repository}'`,
});

const files = (extra = {}, repository = REPO) =>
  Object.entries({ ...holding(repository), ...extra }).map(([path, text]) => ({ path, text }));

const problems = (extra, options = {}) => findProblems({ files: files(extra), repository: REPO, formerOwners: ["old-org"], ...options });

test("places that agree, and files that name no owner, are clean", () => {
  assert.deepEqual(problems({ "apps/tray/Weir.Tray/Other.cs": "var url = ProjectLocation.RepositoryUrl;" }), []);
});

test("every place the repository is kept is found in the real repository's list, once each", () => {
  assert.equal(new Set(PLACES.map((place) => place.path)).size, PLACES.length);
  assert.equal(PLACES.length, 4);
});

test("the real Node place reads as owner/name, and no former owner is the current one", () => {
  assert.match(REPOSITORY, /^[^/\s]+\/[^/\s]+$/);
  const owner = REPOSITORY.split("/")[0].toLowerCase();
  assert.ok(FORMER_OWNERS.every((former) => former.toLowerCase() !== owner));
});

test("a place that differs from the Node place is named, with what it holds", () => {
  const result = findProblems({ files: files({}, "other-org/Weir"), repository: REPO, formerOwners: [] });
  assert.equal(result.length, PLACES.length);
  assert.match(result[0], /^apps\/server\/src\/Weir\.Core\/Updates\/ProjectLocation\.cs:1: holds other-org\/Weir, but scripts\/repository\.mjs holds new-org\/Weir\./);
});

test("a place that is missing is a problem too", () => {
  const present = files().filter((file) => file.path !== "scripts/repository.ps1");
  assert.deepEqual(findProblems({ files: present, repository: REPO, formerOwners: [] }), [
    "scripts/repository.ps1:1: holds no repository, but scripts/repository.mjs holds new-org/Weir.",
  ]);
});

test("code that writes the current or a former owner is named by line, whatever the case", () => {
  const result = problems({
    "apps/server/src/X.cs": "var a = 1;\nvar url = \"https://github.com/New-Org/Weir/releases\";",
    "scripts/y.mjs": "const old = 'old-org/Weir';",
    ".github/workflows/z.yml": "image: ghcr.io/new-org/weir",
  });
  const instead = "read the repository from its language's place instead";
  assert.deepEqual(result, [
    `apps/server/src/X.cs:2: writes the owner new-org; ${instead}.`,
    `scripts/y.mjs:1: writes the owner old-org; ${instead}.`,
    `.github/workflows/z.yml:1: writes the owner new-org; ${instead}.`,
  ]);
});

test("text may name the current owner but not a former one", () => {
  assert.deepEqual(problems({ "README.md": "Install from https://github.com/new-org/Weir/releases." }), []);
  assert.deepEqual(problems({ "docs/install-docker.md": "ok\nImage ghcr.io/old-org/weir:1.0.0" }), [
    "docs/install-docker.md:2: names the former owner old-org.",
  ]);
  assert.deepEqual(problems({ "compose.yaml": "image: ghcr.io/Old-Org/weir" }), ["compose.yaml:1: names the former owner old-org."]);
});

test("before any move, text is not searched at all", () => {
  assert.deepEqual(problems({ "README.md": "see old-org/Weir" }, { formerOwners: [] }), []);
});

test("history, the plan and the files naming the person are left alone", () => {
  assert.deepEqual(
    problems({
      "CHANGELOG.md": "old-org/Weir",
      "docs/release-notes/v1.0.0.md": "ghcr.io/old-org/weir",
      "docs/archive/plan.md": "old-org/Weir",
      "docs/archive/PLAN-ORG-MOVE.md": "old-org/Weir",
      ".github/CODEOWNERS": "* @old-org",
      ".github/FUNDING.yml": "github: [old-org]",
    }),
    [],
  );
});

test("the copyright holder, a sponsorship link and a link to one of Weir's issues are left alone in code and text alike", () => {
  assert.deepEqual(
    problems({
      "apps/server/Directory.Build.props": "<Copyright>Copyright (C) 2026 old-org and the Weir contributors</Copyright>",
      "apps/web/.env.example": "# VITE_SUPPORT_URL=https://github.com/sponsors/old-org",
      "apps/server/tests/T.cs": '/// <seealso href="https://github.com/old-org/Weir/issues/497"/>',
      "docs/engineering/note.md": "[#503](https://github.com/old-org/Weir/pull/503)",
    }),
    [],
  );
});

test("a build that runs in another repository than the code holds fails, whatever the case", () => {
  assert.deepEqual(problems({}, { running: "NEW-ORG/weir" }), []);
  assert.deepEqual(problems({}, { running: "third-org/Weir" }), [
    "scripts/repository.mjs:1: the code holds new-org/Weir, but this build runs in third-org/Weir. Sweep the move (docs/archive/PLAN-ORG-MOVE.md).",
  ]);
});
