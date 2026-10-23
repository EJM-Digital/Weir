#!/usr/bin/env node
// Dead-code guard for Weir (#328): a refactor that changes behaviour must not leave the code it
// replaced in the tree.
//
//   web    — Knip for files, exports and types nothing imports (this script).
//   web    — phantom `mm-*` class names: markup styled by nothing (this script). The mirror
//            image of an unused export, and the same failure — code that reads as if it does
//            something and does not.
//   server — the .NET build itself: apps/server/.editorconfig raises IDE0051 (unused private
//            members) and IDE0052 (private members never read) to warnings, and the build treats
//            warnings as errors, alongside the recommended CA analyzers (e.g. CA1812, internal
//            classes nothing instantiates). `dotnet build apps/server/Weir.slnx -warnaserror` is
//            that half of the guard, so it is not repeated here.
//
// The Knip half compares against `scripts/dead-code-allowlist.json` and fails only on entries
// that are *not* listed. The allowlist is the deliberate part: something kept on
// purpose gets a line and a reason, and anything else is a build failure. Keeping a
// silent baseline instead would just be the same accumulation with extra steps.
//
// The phantom-class half has no allowlist on purpose — see the note above that check.

import { execFileSync } from "node:child_process";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

export function webUnusedExports(repo) {
  const WEB = path.join(repo, "apps", "web");
  // Knip's own JS entry, run with this node. Avoids `npx` (a shell on Windows, which node refuses
  // to spawn without one) and the .bin/*.cmd shim entirely. What counts as an entry point, a
  // reference or generated code is apps/web/knip.json.
  const entry = path.join(WEB, "node_modules", "knip", "bin", "knip.js");
  if (!existsSync(entry)) {
    return { skipped: "apps/web/node_modules is missing (run npm ci)" };
  }
  let raw = "";
  try {
    raw = execFileSync(process.execPath, [entry, "--include", "files,exports,types", "--reporter", "json"], {
      cwd: WEB,
      encoding: "utf8",
      maxBuffer: 64 * 1024 * 1024,
    });
  } catch (err) {
    // Knip exits non-zero when it reports findings; the findings are on stdout.
    raw = err.stdout ?? "";
    if (!raw) throw err;
  }
  // One finding per unreferenced file, export or type: a file is keyed by its path, an export or type by
  // "path:name". That key is also the allowlist's.
  return {
    findings: JSON.parse(raw).issues.flatMap((issue) => {
      const file = issue.file.replace(/\\/g, "/");
      const members = [...issue.exports, ...issue.types].map((item) => `${file}:${item.name}`);
      return [...issue.files.map(() => file), ...members];
    }),
  };
}

// A class name on an element that no stylesheet has a rule for. #590 (the route error screen,
// eight invented names, the heading pushed below the fold) and #594 (the loading skeleton,
// placeholder divs collapsed to zero size) were both this, both shipped, and both sat in the
// tree for a long time because nothing was looking.
//
// The check is deliberately narrow, and each narrowing is what makes it trustworthy:
//
//   `mm-` only        — Weir's own namespace. Tailwind generates utilities prefixed by what they
//                       set (`bg-`, `text-`, `p-`), never a bare `mm-*`, so "absent from our CSS"
//                       means "styled by nothing" without the checker knowing any Tailwind at all.
//                       That is also why this needs no build: the stylesheet sources are the
//                       whole truth for this namespace.
//   plain literals    — `className="…"` only, never `{`…`}` or `{expr}`. Generated modifiers like
//                       `mm-activity-event-icon--${tone}` are the one honest source of noise: the
//                       base rule *is* the default tone, so `--info` correctly has no rule of its
//                       own. Excluding interpolation drops all of them structurally, instead of
//                       an allowlist that would need a line for every new tone.
//   line by line      — a `className` Prettier split across lines is missed. Accepted: it
//                       under-reports rather than crying wolf.
//
// Hence no allowlist. A finding here is a defect, not a judgement call: either the rule was never
// written or the name is a typo. Measured over the repo's history this has found 26 occurrences
// and zero false positives, so an escape hatch would only ever be used to wave a real bug through.
export function phantomClassNames(repo) {
  const WEB = path.join(repo, "apps", "web");
  const SRC = path.join(WEB, "src");

  const files = [];
  (function walk(dir) {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, entry.name);
      if (entry.isDirectory()) walk(p);
      else files.push(p);
    }
  })(SRC);

  // Every mm-* name any stylesheet mentions, in a selector or anywhere else. Being generous here
  // is the right way to be wrong: it can only ever hide a finding, never invent one.
  const styled = new Set();
  for (const file of files.filter((f) => f.endsWith(".css"))) {
    const css = readFileSync(file, "utf8").replace(/\/\*[\s\S]*?\*\//g, " ");
    for (const m of css.matchAll(/\.(mm-[\w-]+)/g)) styled.add(m[1]);
  }

  const findings = [];
  let literals = 0;
  for (const file of files.filter((f) => f.endsWith(".tsx"))) {
    // Tests render fixtures and assert on markup; a class name there is not a styling claim.
    if (file.endsWith(".test.tsx")) continue;
    const rel = path.relative(repo, file).split(path.sep).join("/");
    readFileSync(file, "utf8")
      .split(/\r?\n/)
      .forEach((line, i) => {
        for (const m of line.matchAll(/className="([^"{}$`]*)"/g)) {
          for (const token of m[1].split(/\s+/)) {
            if (!/^mm-[\w-]+$/.test(token)) continue;
            literals += 1;
            if (styled.has(token)) continue;
            findings.push({ at: `${rel}:${i + 1}`, token });
          }
        }
      });
  }
  return { findings, literals, styled: styled.size };
}

export function report(label, findings, allowed, reasons, log = console.error) {
  const unexpected = findings.filter((f) => !allowed.has(f));
  const stale = [...allowed].filter((a) => !findings.includes(a));
  if (unexpected.length) {
    log(`[dead-code] ${label}: ${unexpected.length} unreferenced item(s) not on the allowlist:`);
    for (const f of unexpected) log(`  ${f}`);
    log(`  Remove them, wire them to a real consumer, or add them to scripts/dead-code-allowlist.json`);
    log(`  with a reason if they are deliberately kept.`);
  }
  if (stale.length) {
    log(`[dead-code] ${label}: allowlist entries that are no longer dead (remove them):`);
    for (const f of stale) log(`  ${f}  — ${reasons[f]}`);
  }
  return unexpected.length + stale.length;
}

/** Runs both halves of the guard over the repository at `repo`; returns the exit code. */
export function main(repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), ".."), out = { log: console.log, error: console.error }) {
  const allow = JSON.parse(readFileSync(path.join(repo, "scripts", "dead-code-allowlist.json"), "utf8"));
  const allowedWeb = new Set(Object.keys(allow.webExports ?? {}));
  let failures = 0;

  const phantom = phantomClassNames(repo);
  if (phantom.findings.length) {
    out.error(`[dead-code] phantom classes: ${phantom.findings.length} mm-* class name(s) that no stylesheet styles:`);
    for (const f of phantom.findings) out.error(`  ${f.at}  ${f.token}`);
    out.error(`  Each one is markup claiming a style that does not exist. Write the rule in`);
    out.error(`  apps/web/src/styles/, or drop the class name. There is no allowlist for these.`);
    failures += phantom.findings.length;
  } else {
    out.log(
      `[dead-code] phantom classes: 0 of ${phantom.literals} literal mm-* use(s) unstyled, ` +
        `against ${phantom.styled} styled name(s).`,
    );
  }

  const web = webUnusedExports(repo);
  if (web.skipped) {
    out.log(`[dead-code] web skipped: ${web.skipped}`);
  } else {
    failures += report("web exports", web.findings, allowedWeb, allow.webExports, out.error);
    out.log(`[dead-code] web exports: ${web.findings.length} unreferenced, ${allowedWeb.size} allowlisted`);
  }

  if (failures) {
    return 1;
  }
  out.log("[dead-code] No unexpected dead code.");
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) process.exitCode = main();
