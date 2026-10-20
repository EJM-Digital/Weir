#!/usr/bin/env node
// Moves Weir to another owner or name in one commit, once the repository has been transferred or renamed
// (docs/archive/PLAN-ORG-MOVE.md):
//
//   node scripts/sweep-repository-owner.mjs --to <owner/name>
//
// It rewrites the places check-repository-owner.mjs lists, adds the old owner to that script's former owners, and
// replaces the old owner in the text that cannot read a constant: documentation, the docs site, the compose file and the
// issue templates. History, the plan, CODEOWNERS and FUNDING.yml, the copyright holder and sponsorship links stay.
// Then run check-repository-owner.mjs: it names whatever is left.
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { PERSON_LINES, PLACES, isExcused, isText } from "./check-repository-owner.mjs";
import { REPOSITORY } from "./repository.mjs";

const NODE_PLACE = "scripts/repository.mjs";
const CHECK = "scripts/check-repository-owner.mjs";

const escaped = (text) => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const everywhere = (text) => new RegExp(escaped(text), "gi");

/** Text with the repository `from` replaced by `to` (both `owner/name`): its address, its image, its docs site and its sibling Deluno. */
export function sweepText(text, from, to) {
  const [fromOwner, fromName] = from.split("/");
  const [toOwner, toName] = to.split("/");
  return text
    .split("\n")
    .map((line) =>
      PERSON_LINES.some((pattern) => pattern.test(line))
        ? line
        : line
            .replace(everywhere(`ghcr.io/${from}`), `ghcr.io/${to.toLowerCase()}`)
            .replace(everywhere(`${fromOwner}.github.io/${fromName}`), `${toOwner.toLowerCase()}.github.io/${toName}`)
            .replace(everywhere(`${fromOwner}.github.io`), `${toOwner.toLowerCase()}.github.io`)
            .replace(everywhere(from), to)
            .replace(everywhere(`${fromOwner}/Deluno`), `${toOwner}/Deluno`)
            .replace(new RegExp(`(organizationName:\\s*')${escaped(fromOwner)}(')`, "gi"), `$1${toOwner}$2`),
    )
    .join("\n");
}

/** The text of a place (a file check-repository-owner.mjs reads the repository from) holding `to` instead of `from`. */
export function sweepPlace(text, from, to) {
  const [fromOwner, fromName] = from.split("/");
  const [toOwner, toName] = to.split("/");
  return text
    .replace(`Owner = "${fromOwner}"`, `Owner = "${toOwner}"`)
    .replace(`Repo = "${fromName}"`, `Repo = "${toName}"`)
    .replace(`"${from}"`, `"${to}"`)
    .replace(`'${from}'`, `'${to}'`)
    .replace(`https://github.com/${from}'`, `https://github.com/${to}'`);
}

/** The check's text with `owner` added to the former owners, unless it is the owner Weir now has. */
export function addFormerOwner(text, owner, current) {
  if (owner.toLowerCase() === current.toLowerCase()) {
    return text;
  }

  return text.replace(/export const FORMER_OWNERS = \[([^\]]*)\];/, (_, listed) => {
    const names = listed.split(",").map((name) => name.trim()).filter(Boolean);
    const quoted = JSON.stringify(owner);
    return `export const FORMER_OWNERS = [${(names.includes(quoted) ? names : [...names, quoted]).join(", ")}];`;
  });
}

function main(argv) {
  const at = argv.indexOf("--to");
  const to = at >= 0 ? argv[at + 1] : undefined;
  if (!to || !/^[A-Za-z0-9-]+\/[A-Za-z0-9._-]+$/.test(to)) {
    console.error("Usage: node scripts/sweep-repository-owner.mjs --to <owner/name>");
    process.exit(2);
  }
  if (to === REPOSITORY) {
    console.log(`Weir is already ${REPOSITORY}; nothing to sweep.`);
    return;
  }

  const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
  const rewrite = (path, change) => {
    const before = readFileSync(resolve(root, path), "utf8");
    const after = change(before);
    if (after !== before) {
      writeFileSync(resolve(root, path), after);
    }
    return after !== before;
  };

  const changed = [];
  for (const path of [NODE_PLACE, ...PLACES.map((place) => place.path)]) {
    if (rewrite(path, (text) => sweepPlace(text, REPOSITORY, to))) {
      changed.push(path);
    }
  }
  if (rewrite(CHECK, (text) => addFormerOwner(text, REPOSITORY.split("/")[0], to.split("/")[0]))) {
    changed.push(CHECK);
  }

  const files = execFileSync("git", ["ls-files", "-z"], { cwd: root, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 }).split("\0").filter(Boolean);
  for (const path of files.filter((file) => isText(file) && !isExcused(file))) {
    if (rewrite(path, (text) => sweepText(text, REPOSITORY, to))) {
      changed.push(path);
    }
  }

  console.log(`${changed.length} files now say ${to} instead of ${REPOSITORY}.`);
  console.log("Next: node scripts/check-repository-owner.mjs, which names anything left.");
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main(process.argv.slice(2));
}
