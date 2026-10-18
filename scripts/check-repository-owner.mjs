#!/usr/bin/env node
// Fails when Weir's repository owner or name is written anywhere but the one place its language keeps it, so moving the
// repository to another owner is one small commit (docs/exec-plans/active/PLAN-ORG-MOVE.md).
//
// The places: scripts/repository.mjs (Node, and the truth the others are compared with), the server's and the tray's
// ProjectLocation.cs, the web app's lib/repository.ts and scripts/repository.ps1 (PowerShell).
//
// Checks:
//   1. Every place holds the same repository as scripts/repository.mjs.
//   2. A source file, a test, a script or a workflow names no owner at all: it reads its language's place.
//   3. Text that cannot read a constant (documentation, the docs site, the compose file, the issue templates) may name the
//      current owner, but not a former one (FORMER_OWNERS), so the rename's search and replace is proved complete.
//   4. With --repository <owner/name> (CI passes github.repository), the places hold the repository the code runs in, so a
//      transfer that nobody swept fails the next build.
//
// Excused: the history (changelog, release notes, archived plans and decisions), the move's own plan, the files that name
// the person rather than the repository (CODEOWNERS, FUNDING.yml), and these lines wherever they are: the copyright
// holder, a sponsorship link and a link to one of Weir's issues or pull requests, which a move leaves working.
//
//   node scripts/check-repository-owner.mjs [--repository owner/name]
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { REPOSITORY } from "./repository.mjs";

/** Owners the repository used to have: text that cannot read a constant must not name them any more. The move adds the old owner here. */
export const FORMER_OWNERS = [];

const csharp = (text) => {
  const owner = /\bOwner\s*=\s*"([^"]+)"/.exec(text)?.[1];
  const repo = /\bRepo\s*=\s*"([^"]+)"/.exec(text)?.[1];
  return owner && repo ? `${owner}/${repo}` : null;
};
const firstGroup = (pattern) => (text) => pattern.exec(text)?.[1] ?? null;

/** The places that hold the repository, each with the way to read it. */
export const PLACES = [
  { path: "apps/server/src/Weir.Core/Updates/ProjectLocation.cs", read: csharp },
  { path: "apps/tray/Weir.Tray/ProjectLocation.cs", read: csharp },
  { path: "apps/web/src/lib/repository.ts", read: firstGroup(/\bREPOSITORY_SLUG\s*=\s*"([^"]+)"/) },
  { path: "scripts/repository.ps1", read: firstGroup(/\$WeirRepository\s*=\s*'([^']+)'/) },
];
const NODE_PLACE = "scripts/repository.mjs";

/** Files that describe the past, the plan, or the person; the reason is why a move leaves them alone. */
export const EXCUSED = [
  { path: "CHANGELOG.md", why: "history" },
  { prefix: "docs/release-notes/", why: "history" },
  { prefix: "docs/archive/", why: "history" },
  { prefix: "docs/adr/", why: "history" },
  { prefix: "apps/server/tests/fixtures/recorded/", why: "real replies kept exactly as they came, so a replay is the real thing" },
  { prefix: "docs-site/docs/architecture/adr/", why: "history" },
  { path: "docs/exec-plans/active/PLAN-ORG-MOVE.md", why: "the plan names the old and the new owner" },
  { path: ".github/CODEOWNERS", why: "names the person who reviews, not the repository" },
  { path: ".github/FUNDING.yml", why: "names the person's sponsorship page" },
  { path: "scripts/scenarios-ci/images.json", why: "names Deluno's image by its registry path, which is Deluno's to move, not Weir's" },
];

/** Lines that name the person rather than where Weir lives: a move leaves them as they are. */
export const PERSON_LINES = [/Copyright \(C\) \d{4}/, /github\.com\/sponsors\//];

/** Lines that link to one of Weir's issues or pull requests, which a move leaves working. */
const CITATION_LINES = [/github\.com\/[^/\s"]+\/Weir\/(issues|pull)\/\d+/];
export const EXCUSED_LINES = [...PERSON_LINES, ...CITATION_LINES];

/** Text that cannot read a constant. */
export const TEXT = [/\.mdx?$/i, /^docs-site\//, /^compose\.yaml$/, /^docker\/\.env\.example$/, /^apps\/web\/\.env\.example$/, /^\.github\/ISSUE_TEMPLATE\//];

export const isExcused = (path) => EXCUSED.some((entry) => (entry.path ? entry.path === path : path.startsWith(entry.prefix)));
export const isText = (path) => TEXT.some((pattern) => pattern.test(path));

/**
 * What is wrong in `files` (`{ path, text }`, paths relative to the repository root), as `path:line: message` strings.
 * @param {{ files: {path: string, text: string}[], repository: string, formerOwners?: string[], running?: string | null }} input
 */
export function findProblems({ files, repository, formerOwners = FORMER_OWNERS, running = null }) {
  const problems = [];
  const owner = repository.split("/")[0];
  const byPath = new Map(files.map((file) => [file.path, file.text]));

  for (const place of PLACES) {
    const text = byPath.get(place.path);
    const held = text === undefined ? null : place.read(text);
    if (held !== repository) {
      problems.push(`${place.path}:1: holds ${held ?? "no repository"}, but ${NODE_PLACE} holds ${repository}.`);
    }
  }

  if (running && running.toLowerCase() !== repository.toLowerCase()) {
    problems.push(`${NODE_PLACE}:1: the code holds ${repository}, but this build runs in ${running}. Sweep the move (docs/exec-plans/active/PLAN-ORG-MOVE.md).`);
  }

  const names = [...new Set([owner, ...formerOwners])].map((name) => name.toLowerCase());
  const former = formerOwners.map((name) => name.toLowerCase());
  const places = new Set([NODE_PLACE, ...PLACES.map((place) => place.path)]);
  for (const { path, text } of files) {
    if (places.has(path) || isExcused(path)) {
      continue;
    }

    const searched = isText(path) ? former : names;
    if (searched.length === 0) {
      continue;
    }

    text.split(/\r?\n/).forEach((line, index) => {
      const lower = line.toLowerCase();
      const found = searched.find((name) => lower.includes(name));
      if (found && !EXCUSED_LINES.some((pattern) => pattern.test(line))) {
        problems.push(
          `${path}:${index + 1}: ${isText(path) ? `names the former owner ${found}` : `writes the owner ${found}; read the repository from its language's place instead`}.`,
        );
      }
    });
  }

  return problems;
}

function repositoryFiles(root) {
  const listing = execFileSync("git", ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], { cwd: root, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
  return listing.split("\0").filter(Boolean);
}

function readText(root, path) {
  try {
    const text = readFileSync(resolve(root, path), "utf8");
    return text.includes("\0") ? null : text;
  } catch {
    return null;
  }
}

function main(argv) {
  const at = argv.indexOf("--repository");
  const running = at >= 0 ? argv[at + 1] : null;
  const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
  const files = repositoryFiles(root).flatMap((path) => {
    const text = readText(root, path);
    return text === null ? [] : [{ path, text }];
  });

  const problems = findProblems({ files, repository: REPOSITORY, running });
  if (problems.length > 0) {
    console.error(problems.join("\n"));
    console.error(`\n${problems.length} problem(s). The owner and name are written once per language; see docs/exec-plans/active/PLAN-ORG-MOVE.md.`);
    process.exit(1);
  }

  console.log(`The repository ${REPOSITORY} is written only where its language keeps it.`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main(process.argv.slice(2));
}
