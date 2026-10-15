// node --test scripts/release/shipped-paths.test.mjs
// Which files make a different product, judged on path names and on real commits in a throwaway repository.
import assert from "node:assert/strict";
import { test } from "node:test";

import { isShippedPath, sameShippedFiles, shippedPaths } from "./shipped-paths.mjs";
import { repository } from "./temporary-repository.mjs";

test("what the installer and the image are built from ships", () => {
  for (const path of [
    "Dockerfile",
    ".dockerignore",
    ".node-version",
    "docker/entrypoint.sh",
    "apps/server/src/Weir.Core/Rules/Plan.cs",
    "apps/server/src/Weir.Host/Properties/PublishProfiles/win-x64.pubxml",
    "apps/server/src/Weir.Api/packages.lock.json",
    "apps/server/Directory.Build.props",
    "apps/server/Directory.Packages.props",
    "apps/server/global.json",
    "apps/web/src/pages/activity/activity-page.tsx",
    "apps/web/src/test-support/not-under-src-test.ts",
    "apps/web/package-lock.json",
    "apps/web/vite.config.ts",
    "apps/web/openapi/weir-openapi.json",
    "apps/web/scripts/compress-assets.mjs",
    "scripts/dev-ports.json",
    "apps/tray/Weir.Tray/TrayApp.cs",
    "apps/tray/Directory.Packages.props",
    "packaging/windows/build-velopack.ps1",
    "packaging/windows/assets/weir-tray-icon.ico",
    "scripts/prune-release-feed.mjs",
    ".github/workflows/release.yml",
    ".github/workflows/ci-packaging.yml",
    ".github/actions/apt-fail-fast/action.yml",
    ".github/actions/setup-ffmpeg-linux/action.yml",
  ]) {
    assert.equal(isShippedPath(path), true, path);
  }
});

test("tests, scripts, docs, CI and the other repository files do not ship", () => {
  for (const path of [
    "apps/server/tests/Weir.Core.Tests/Rules/PlanTests.cs",
    "apps/server/tools/Weir.LiveAudit/Program.cs",
    "apps/server/Weir.slnx",
    "apps/server/README.md",
    "apps/tray/Weir.Tray.Tests/TrayStateTests.cs",
    "apps/tray/Weir.Tray.StandInServer/Program.cs",
    "apps/tray/Weir.Tray.slnx",
    "apps/web/src/pages/activity/activity-page.test.tsx",
    "apps/web/src/test/setup.ts",
    "apps/web/scripts/compress-assets.test.mjs",
    "apps/web/scripts/run-dev-stack.mjs",
    "apps/web/README.md",
    "apps/web/eslint.config.js",
    "docs/release.md",
    "docs/release-notes/v1.0.0-rc.18.md",
    "docs-site/docs/quickstart.md",
    "CHANGELOG.md",
    "README.md",
    "AGENTS.md",
    "compose.yaml",
    "docker/README.md",
    "docker/.env.example",
    "scripts/scenarios/Invoke-WeirScenarios.ps1",
    "scripts/verify-golden-path-for-release.mjs",
    "scripts/release/shipped-paths.mjs",
    "scripts/check-agent-docs.mjs",
    ".github/workflows/ci.yml",
    ".github/workflows/ci-contract.yml",
    ".github/workflows/ci-stress.yml",
    ".github/workflows/audit.yml",
    ".github/workflows/codeql.yml",
    ".github/workflows/docs.yml",
    ".github/actions/setup-real-media-tools/action.yml",
    ".github/dependabot.yml",
    "packaging/brand/weir-mark.svg",
  ]) {
    assert.equal(isShippedPath(path), false, path);
  }
});

test("Windows separators and a leading ./ name the same files", () => {
  assert.equal(isShippedPath("apps\\server\\src\\Weir.Core\\Plan.cs"), true);
  assert.equal(isShippedPath("./Dockerfile"), true);
  assert.equal(isShippedPath("docs\\release.md"), false);
});

test("a folder name is not a prefix match on a longer name", () => {
  assert.equal(isShippedPath("apps/web-old/src/a.ts"), false);
  assert.equal(isShippedPath("Dockerfile.dev"), false);
  assert.equal(isShippedPath("packaging/windows-notes.md"), false);
});

test("shippedPaths keeps only the files that ship, in order", () => {
  assert.deepEqual(shippedPaths(["docs/a.md", "apps/web/src/a.ts", "scripts/x.mjs", "Dockerfile"]), ["apps/web/src/a.ts", "Dockerfile"]);
});

// --- real commits ------------------------------------------------------------------------------------------------

const base = { "apps/server/src/Weir.Core/Plan.cs": "a", "docs/a.md": "a", "apps/server/Directory.Build.props": "0.0.1-dev" };

test("a commit is the same product as itself", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  assert.equal(sameShippedFiles(x, x, git).ok, true);
});

test("a change to tests, scripts, docs or CI keeps the shipped files the same", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  const y = commit(
    {
      "apps/server/tests/PlanTests.cs": "t",
      "apps/web/src/pages/a.test.tsx": "t",
      "scripts/check.mjs": "s",
      "docs/release-notes/v1.0.0-rc.19.md": "n",
      ".github/workflows/ci.yml": "c",
    },
    "tests, scripts, docs and CI",
  );
  const result = sameShippedFiles(x, y, git);
  assert.equal(result.ok, true);
  assert.deepEqual(result.shipped, []);
  assert.match(result.reason, /only tests, scripts, docs or CI files changed \(5 files\)/);
});

test("a change to a file that ships makes a different product", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  commit({ "docs/b.md": "b" }, "docs");
  const y = commit({ "apps/server/src/Weir.Core/Plan.cs": "b" }, "code");
  const result = sameShippedFiles(x, y, git);
  assert.equal(result.ok, false);
  assert.deepEqual(result.shipped, ["apps/server/src/Weir.Core/Plan.cs"]);
  assert.match(result.reason, /1 file that ships changed: apps\/server\/src\/Weir\.Core\/Plan\.cs/);
});

test("a change to the release workflow, the packaging workflow or an action they use makes a different product", () => {
  for (const path of [
    ".github/workflows/release.yml",
    ".github/workflows/ci-packaging.yml",
    ".github/actions/apt-fail-fast/action.yml",
    ".github/actions/setup-ffmpeg-linux/action.yml",
  ]) {
    const { git, commit } = repository();
    const x = commit({ ...base, [path]: "a" }, "base");
    const y = commit({ [path]: "b" }, "workflow");
    const result = sameShippedFiles(x, y, git);
    assert.equal(result.ok, false, path);
    assert.deepEqual(result.shipped, [path]);
  }
});

test("a change to a workflow that only tests or checks does not make a different product", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  const y = commit(
    {
      ".github/workflows/ci.yml": "b",
      ".github/workflows/ci-contract.yml": "b",
      ".github/workflows/audit.yml": "b",
      ".github/actions/setup-real-media-tools/action.yml": "b",
    },
    "test workflows",
  );
  assert.equal(sameShippedFiles(x, y, git).ok, true);
});

test("a version change makes a different product", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  const y = commit({ "apps/server/Directory.Build.props": "1.0.0-rc.19" }, "version");
  const result = sameShippedFiles(x, y, git);
  assert.equal(result.ok, false);
  assert.deepEqual(result.shipped, ["apps/server/Directory.Build.props"]);
});

test("a shipped file changed and changed back is the same product", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  commit({ "apps/server/src/Weir.Core/Plan.cs": "b" }, "change");
  const y = commit({ "apps/server/src/Weir.Core/Plan.cs": "a" }, "revert");
  assert.equal(sameShippedFiles(x, y, git).ok, true);
});

test("the reason names at most five files and counts the rest", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  const files = Object.fromEntries(Array.from({ length: 7 }, (_, i) => [`apps/server/src/Weir.Core/F${i}.cs`, "x"]));
  const result = sameShippedFiles(x, commit(files, "many"), git);
  assert.equal(result.ok, false);
  assert.equal(result.shipped.length, 7);
  assert.match(result.reason, /7 files that ship changed: .*F4\.cs, and 2 more/);
});

test("a commit that is not an ancestor never counts, even when no shipped file differs", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  git(["checkout", "-q", "-b", "side"]);
  const side = commit({ "docs/side.md": "s" }, "side");
  git(["checkout", "-q", "main"]);
  const y = commit({ "docs/main.md": "m" }, "main");
  const result = sameShippedFiles(side, y, git);
  assert.equal(result.ok, false);
  assert.match(result.reason, /is not an earlier commit of/);
  // A newer commit is not an ancestor of an older one either.
  assert.equal(sameShippedFiles(y, x, git).ok, false);
});

test("a commit this clone does not hold never counts", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  assert.equal(sameShippedFiles("f".repeat(40), x, git).ok, false);
});
