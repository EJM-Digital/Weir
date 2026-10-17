#!/usr/bin/env node
// The one rule for which files "ship". A change to any of them makes a different product, so a scenario-suite pass
// recorded on an earlier commit no longer counts for it. Everything else (tests, scripts that only check or run
// things, docs, CI files) cannot change what a person installs, so a pass carries over it (the owner, 16 Oct 2026).
//
// The list is read off what the two deliverables are built from, not guessed:
//
//   Docker image   Dockerfile COPY lines: apps/web (the production web build), scripts/dev-ports.json (the web build
//                  reads it), apps/server's solution-level build files and apps/server/src, docker/entrypoint.sh;
//                  .dockerignore decides what the build context holds; .node-version picks the Node that builds the web app.
//   Windows setup  packaging/windows/build-velopack*.ps1 publishes apps/server/src/Weir.Host (self-contained, through its
//                  publish profile) and apps/tray/Weir.Tray, builds apps/web, bundles the pinned tools and runs vpk; the
//                  packaging scripts call scripts/prune-release-feed.mjs, find-previous-release.mjs (and its semver.mjs)
//                  and pinned-download.mjs, so those shape the package. Weir.Tray.csproj embeds
//                  packaging/windows/assets/weir-tray-icon.ico.
//   Version        no file carries the release version (the tag stamps it at build time), but
//                  apps/server/Directory.Build.props holds the placeholder and every build rule, so it ships.
//
// Not shipped, whatever their folder: every *.test.* file, apps/web/src/test, the test projects and tools under
// apps/server and apps/tray, the stand-in server, solution files, READMEs, docs, docs-site, release notes, compose.yaml
// (a file people copy; the image does not read it), the other scripts and the workflows that only test or check
// (ci.yml, ci-contract.yml, ci-stress.yml, audit, codeql, docs, dependabot).
//
// A workflow that builds or packages what ships counts as shipped, because changing how the installer or the image is
// made can change what lands in it: release.yml (builds and publishes both), ci-packaging.yml (the reusable workflow
// that builds the Windows package the scenario suite installs, and the Docker candidate), and the composite actions
// those build steps use (apt-fail-fast, setup-ffmpeg-linux). ci.yml itself only decides when ci-packaging.yml runs and
// passes it the version, so it does not count; setup-real-media-tools is used by test jobs alone.
//
// It errs on the side of "ships": a path is exempt only when this file says the build never reads it. Test helpers that
// sit beside source files (a *-fixtures.ts, a *-test-support.tsx) are not exempt: only *.test.* files and src/test are.
//
// Usage:  node scripts/release/shipped-paths.mjs --base <sha> --head <sha>   (exit 0 when nothing that ships differs)
import { execFileSync } from "node:child_process";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

// Paths that ship: a trailing "/" is a whole folder, anything else is one file.
export const SHIPPED = [
  // The Docker image.
  "Dockerfile",
  ".dockerignore",
  ".node-version",
  "docker/entrypoint.sh",
  // The server, in both deliverables.
  "apps/server/src/",
  "apps/server/global.json",
  "apps/server/Directory.Build.props",
  "apps/server/Directory.Packages.props",
  "apps/server/NuGet.Config",
  "apps/server/.editorconfig",
  // The web app, in both deliverables (and the file its build reads from outside its folder).
  "apps/web/",
  "scripts/dev-ports.json",
  // The Windows tray and package.
  "apps/tray/Weir.Tray/",
  "apps/tray/Directory.Build.props",
  "apps/tray/Directory.Packages.props",
  "apps/tray/NuGet.Config",
  "apps/tray/.editorconfig",
  "packaging/windows/",
  "scripts/prune-release-feed.mjs",
  "scripts/find-previous-release.mjs",
  "scripts/semver.mjs",
  "scripts/pinned-download.mjs",
  // The workflows and actions that build or package what ships.
  ".github/workflows/release.yml",
  ".github/workflows/ci-packaging.yml",
  ".github/actions/apt-fail-fast/",
  ".github/actions/setup-ffmpeg-linux/",
];

// Inside a shipped folder, what the build never reads.
const NOT_SHIPPED = [
  /\.test\.[cm]?[jt]sx?$/,
  /^apps\/web\/src\/test\//,
  /^apps\/web\/scripts\/run-(api-dev|dev-stack)\.mjs$/,
  /^apps\/web\/(README\.md|\.env\.example|knip\.json|eslint\.config\.js|dependency-audit-exceptions\.json|tsconfig\.tsbuildinfo)$/,
];

export function isShippedPath(path) {
  const file = String(path).replaceAll("\\", "/").replace(/^\.\//, "");
  const listed = SHIPPED.some((entry) => (entry.endsWith("/") ? file.startsWith(entry) : file === entry));
  return listed && !NOT_SHIPPED.some((pattern) => pattern.test(file));
}

export function shippedPaths(paths) {
  return paths.filter(isShippedPath);
}

const runGit = (args) => execFileSync("git", args, { encoding: "utf8", maxBuffer: 256 * 1024 * 1024, stdio: ["ignore", "pipe", "pipe"] });

// Whether `base` is an ancestor of `head`. A commit that is not in this clone is not a yes either.
export function isAncestor(base, head, git = runGit) {
  try {
    git(["merge-base", "--is-ancestor", base, head]);
    return true;
  } catch {
    return false;
  }
}

// Every path whose content differs between the two commits' trees (a rename counts as both of its paths).
export function changedPaths(base, head, git = runGit) {
  return git(["diff", "--name-only", "--no-renames", "-z", base, head]).split("\0").filter(Boolean);
}

// Whether a suite pass on `base` counts for `head`: `base` must be an ancestor of `head` and no file that ships may
// differ between them. Returns { ok, reason, shipped }; `reason` is plain words for the person reading the release log.
export function sameShippedFiles(base, head, git = runGit) {
  if (base === head) return { ok: true, reason: "it is the same commit", shipped: [] };
  if (!isAncestor(base, head, git)) {
    return { ok: false, reason: `${base} is not an earlier commit of ${head}`, shipped: [] };
  }
  let changed;
  try {
    changed = changedPaths(base, head, git);
  } catch (error) {
    return { ok: false, reason: `the changes between ${base} and ${head} could not be read (${String(error.message).split("\n")[0]})`, shipped: [] };
  }
  const shipped = shippedPaths(changed);
  if (shipped.length === 0) {
    return { ok: true, reason: `only tests, scripts, docs or CI files changed (${changed.length} file${changed.length === 1 ? "" : "s"})`, shipped };
  }
  const named = shipped.slice(0, 5).join(", ") + (shipped.length > 5 ? `, and ${shipped.length - 5} more` : "");
  return { ok: false, reason: `${shipped.length} file${shipped.length === 1 ? "" : "s"} that ship${shipped.length === 1 ? "s" : ""} changed: ${named}`, shipped };
}

function main() {
  const argv = process.argv.slice(2);
  const option = (flag) => (argv.includes(flag) ? argv[argv.indexOf(flag) + 1] : undefined);
  const base = option("--base");
  const head = option("--head");
  if (!base || !head) {
    console.error("Usage: node scripts/release/shipped-paths.mjs --base <sha> --head <sha>");
    process.exit(2);
  }
  const result = sameShippedFiles(base, head);
  console.log(result.ok ? `Same shipped files: ${result.reason}.` : `Different shipped files: ${result.reason}.`);
  process.exit(result.ok ? 0 : 1);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) main();
