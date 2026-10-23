#!/usr/bin/env node
// The root .node-version file is the single source of truth for which Node major every setup-node
// step (node-version-file: .node-version) and the Dockerfile's web build stage run. This fails the
// build when the Dockerfile's node major drifts from .node-version, the way #788 briefly did by
// bumping only the Dockerfile.

import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const DOCKERFILE_NODE_IMAGE = /^FROM\s+node:(\d+)[^\s]*/m;

/** The check over the repository at `repo`: whether the two agree, and the line to print either way. */
export function checkNodeDockerVersion(repo) {
  const raw = readFileSync(path.join(repo, ".node-version"), "utf8").trim();
  const expected = raw.match(/^v?(\d+)/)?.[1];
  if (!expected) {
    return { ok: false, message: `.node-version does not start with a version number: "${raw}"` };
  }
  const image = readFileSync(path.join(repo, "Dockerfile"), "utf8").match(DOCKERFILE_NODE_IMAGE);
  if (!image) {
    return { ok: false, message: "Dockerfile has no `FROM node:<major>...` line to check." };
  }
  const actual = image[1];
  if (expected !== actual) {
    return {
      ok: false,
      message:
        `Dockerfile's node image is major ${actual}, but .node-version pins ${expected}. ` +
        "Update the Dockerfile's FROM line (and its digest) to match .node-version, or vice versa.",
    };
  }
  return { ok: true, message: `Dockerfile's node image (major ${actual}) matches .node-version.` };
}

/** Prints the result for the repository at `repo`; returns the exit code. */
export function main(repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), ".."), out = { log: console.log, error: console.error }) {
  const { ok, message } = checkNodeDockerVersion(repo);
  (ok ? out.log : out.error)(message);
  return ok ? 0 : 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) process.exitCode = main();
