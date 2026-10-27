// node scripts/publish-release.mjs <tag> <notes file> <prerelease true|false> <file>...
// Publishes the GitHub Release for <tag> on a repository whose releases are immutable: once a release is published,
// nothing can be attached to it. So the release is made as a draft, every file is attached to the draft, the draft is
// checked to hold every file at its size, and only then is it published. A run that failed part-way leaves a draft,
// which the next run finishes; a release that is already published cannot be changed, so the run stops and says so.
import { execFileSync } from "node:child_process";
import { statSync } from "node:fs";
import { basename } from "node:path";
import { fileURLToPath } from "node:url";

/** What the draft is missing: files not attached, or attached at a different size. */
export function missingFromDraft(files, assets) {
  const attached = new Map(assets.map((asset) => [asset.name, asset.size]));
  return files
    .filter((file) => attached.get(file.name) !== file.size)
    .map((file) => file.name);
}

/**
 * Publishes <tag>. <gh> runs the GitHub CLI with the given arguments and returns its standard output, throwing when
 * it fails; <files> are { path, name, size }.
 */
export function publish({ tag, notesFile, prerelease, files, gh }) {
  const existing = readRelease(tag, gh);
  if (existing && !existing.isDraft) {
    throw new Error(
      `${tag} is already published, and published releases on this repository cannot be changed. Release under a new tag.`,
    );
  }
  if (!existing) {
    const args = [
      "release",
      "create",
      tag,
      "--draft",
      "--verify-tag",
      "--title",
      tag,
      "--notes-file",
      notesFile,
    ];
    gh(prerelease ? [...args, "--prerelease"] : args);
  }

  const attachedBefore = readRelease(tag, gh)?.assets ?? [];
  const toAttach = files.filter(
    (file) => missingFromDraft([file], attachedBefore).length > 0,
  );
  if (toAttach.length > 0) {
    gh([
      "release",
      "upload",
      tag,
      "--clobber",
      ...toAttach.map((file) => file.path),
    ]);
  }

  const missing = missingFromDraft(files, readRelease(tag, gh)?.assets ?? []);
  if (missing.length > 0) {
    throw new Error(
      `The ${tag} draft is still missing ${missing.join(", ")}, so it was not published.`,
    );
  }

  gh([
    "release",
    "edit",
    tag,
    "--draft=false",
    ...(prerelease ? ["--prerelease"] : ["--latest"]),
  ]);
}

function readRelease(tag, gh) {
  try {
    return JSON.parse(gh(["release", "view", tag, "--json", "isDraft,assets"]));
  } catch (error) {
    if (/release not found/i.test(String(error.stderr ?? error.message))) {
      return null;
    }
    throw error;
  }
}

/** Runs the GitHub CLI and returns its standard output; `execute` is execFileSync, which a test replaces. */
export function runGh(args, execute = execFileSync) {
  return execute("gh", args, {
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
  });
}

/** Publishes the release the command line names (tag, notes file, prerelease, files); `gh` and `log` are what a test replaces. */
export function main(argv, { gh = runGh, log = console.log } = {}) {
  const [tag, notesFile, prerelease, ...paths] = argv;
  const files = paths.map((path) => ({
    path,
    name: basename(path),
    size: statSync(path).size,
  }));
  publish({
    tag,
    notesFile,
    prerelease: prerelease === "true",
    files,
    gh,
  });
  log(`Published ${tag} with ${files.length} files.`);
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  main(process.argv.slice(2));
}
