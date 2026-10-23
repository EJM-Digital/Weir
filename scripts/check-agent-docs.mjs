#!/usr/bin/env node
// Checks the repository's Markdown documentation:
//   1. the files agents are pointed at exist;
//   2. every relative link in the root *.md files and in docs/, docs-site/docs and docker/ resolves;
//   3. no current doc presents a retired UI name as if it were still on screen.
// It reads files only, so it runs in well under a second.
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

export const requiredFiles = [
  "AGENTS.md",
  "ARCHITECTURE.md",
  "docs/README.md",
  "docs/agent-harness.md",
  "docs/exec-plans/README.md",
  "docs/file-lifecycle-contract.md",
  "docs/release-governance.md",
];

// Directories whose Markdown is checked, recursively, besides the root *.md files.
const markdownDirs = ["docs", "docs-site/docs", "docker"];

// Docs that record the past rather than describe the current UI. Links in them are still checked;
// the retired-term check skips them.
const historicalPrefixes = [
  "docs/archive/",
  "docs/release-notes/",
  "docs/adr/",
  "docs-site/docs/architecture/adr/",
];
const historicalFiles = new Set(["CHANGELOG.md"]);

// UI names Weir no longer has. A current doc that uses one is describing a screen that is gone.
// A line that names one on purpose, to say it is gone, ends with this HTML comment (invisible when rendered):
const retiredUiAllowMarker = "<!-- retired-ui: history -->";
export const retiredUiTerms = [
  { label: "Housekeeping", pattern: /Housekeeping/ },
  { label: "display density", pattern: /display density/i },
  { label: "Settings › General", pattern: /Settings\s*(?:›|→|->)\s*General\b/ },
  { label: "Settings › Upgrade", pattern: /Settings\s*(?:›|→|->)\s*Upgrade\b/ },
  { label: "Settings › Logs", pattern: /Settings\s*(?:›|→|->)\s*Logs\b/ },
  { label: "Settings › Support", pattern: /Settings\s*(?:›|→|->)\s*Support\b/ },
  { label: "Processing ›", pattern: /Processing\s*(?:›|→|->)/ },
  { label: "Home screen", pattern: / Home screen/ },
  { label: "History page (now Activity)", pattern: /\bHistory (?:page|screen)\b|\b(?:in|from|on|to) History\b/ },
];

function toPosix(rel) {
  return rel.split(path.sep).join("/");
}

function collectMarkdown(repoRoot, dirRel, out) {
  const abs = path.join(repoRoot, dirRel);
  if (!existsSync(abs)) {
    return;
  }
  for (const entry of readdirSync(abs, { withFileTypes: true })) {
    if (entry.name === "node_modules" || entry.name.startsWith(".")) {
      continue;
    }
    const childRel = path.join(dirRel, entry.name);
    if (entry.isDirectory()) {
      collectMarkdown(repoRoot, childRel, out);
    } else if (/\.mdx?$/.test(entry.name)) {
      out.push(toPosix(childRel));
    }
  }
}

/** Removes fenced code blocks and inline code, which hold examples rather than links or UI names. */
export function proseOnly(text) {
  return text
    .replace(/^( {0,3})(```|~~~)[^\n]*\n[\s\S]*?^\1\2[^\n]*$/gm, "")
    .replace(/`[^`\n]*`/g, "");
}

const markdownLinkPattern = /!?\[[^\]]*]\(([^)\s]+)(?:\s+"[^"]*")?\)/g;

/** Docusaurus resolves extensionless doc links and site-absolute paths; mirror that for docs-site pages. */
export function candidatesFor(repoRoot, rel, dir, target) {
  const docsSiteRoot = path.join(repoRoot, "docs-site");
  const inDocsSite = rel.startsWith("docs-site/");
  const bases = [];
  if (target.startsWith("/")) {
    if (!inDocsSite) {
      return [path.join(repoRoot, target)];
    }
    const withoutBase = target.replace(/^\/Weir\//, "/");
    bases.push(path.join(docsSiteRoot, "static", withoutBase));
    if (withoutBase.startsWith("/docs/")) {
      bases.push(path.join(docsSiteRoot, withoutBase));
    }
  } else {
    bases.push(path.resolve(dir, target));
  }
  if (!inDocsSite) {
    return bases;
  }
  return bases.flatMap((base) => [base, `${base}.md`, `${base}.mdx`, path.join(base, "index.md")]);
}

/** The documentation problems under `repoRoot`, and how many Markdown files were checked. */
export function checkDocs(repoRoot) {
  const failures = [];

  for (const rel of requiredFiles) {
    if (!existsSync(path.join(repoRoot, rel))) {
      failures.push(`Missing required agent documentation file: ${rel}`);
    }
  }

  const markdownFiles = readdirSync(repoRoot).filter(
    (name) => name.endsWith(".md") && statSync(path.join(repoRoot, name)).isFile(),
  );
  for (const dir of markdownDirs) {
    collectMarkdown(repoRoot, dir, markdownFiles);
  }

  const readme = readFileSync(path.join(repoRoot, "README.md"), "utf8");
  for (const legacyUpgradeText of [
    "Weir Updater` service",
    "predates the updater service",
  ]) {
    if (readme.includes(legacyUpgradeText)) {
      failures.push(`README.md contains obsolete pre-v2.3 Windows updater guidance: ${legacyUpgradeText}`);
    }
  }

  for (const rel of markdownFiles) {
    const abs = path.join(repoRoot, rel);
    const dir = path.dirname(abs);
    const prose = proseOnly(readFileSync(abs, "utf8"));

    for (const match of prose.matchAll(markdownLinkPattern)) {
      const target = match[1].trim().replace(/^<|>$/g, "");
      if (
        !target ||
        target.startsWith("#") ||
        /^[a-z][a-z0-9+.-]*:/i.test(target)
      ) {
        continue;
      }
      const withoutAnchor = target.split("#")[0].split("?")[0];
      if (!withoutAnchor) {
        continue;
      }
      const decoded = decodeURIComponent(withoutAnchor);
      const found = candidatesFor(repoRoot, rel, dir, decoded).some(
        (candidate) => candidate.startsWith(repoRoot) && existsSync(candidate),
      );
      if (!found) {
        failures.push(`${rel} links to missing local target: ${target}`);
      }
    }

    const historical =
      historicalFiles.has(rel) || historicalPrefixes.some((prefix) => rel.startsWith(prefix));
    if (historical) {
      continue;
    }
    const lines = prose.split("\n");
    lines.forEach((line, index) => {
      if (line.includes(retiredUiAllowMarker)) {
        return;
      }
      for (const term of retiredUiTerms) {
        if (term.pattern.test(line)) {
          failures.push(`${rel}:${index + 1} names retired UI "${term.label}" as if it were current`);
        }
      }
    });
  }

  return { failures, checked: markdownFiles.length };
}

/** Prints the problems under `repoRoot`; returns the exit code. */
export function main(repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), ".."), log = console.error) {
  const { failures, checked } = checkDocs(repoRoot);
  for (const message of failures) {
    log(`[agent-docs] ${message}`);
  }
  if (failures.length > 0) {
    log(`[agent-docs] Failed with ${failures.length} documentation issue(s).`);
    return 1;
  }
  log(`[agent-docs] Agent documentation map is valid (${checked} Markdown files checked).`);
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) process.exitCode = main();
