// node scripts/check-npm-audit.mjs <folder>...
// Runs `npm audit --omit=dev --json` in each folder and fails on any high or critical advisory, except one listed in
// npm-audit-exceptions.json for that folder. An exception has to say why the advisory does not reach anyone, and expires:
// past its date, or once npm offers a fix, it stops excusing the advisory and the check fails until the entry is
// renewed or removed.
import { execSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const Blocking = new Set(["high", "critical"]);

/** The blocking advisories in an `npm audit --json` report: one entry per advisory and package. */
export function blockingAdvisories(report) {
  const found = new Map();
  for (const [name, entry] of Object.entries(report.vulnerabilities ?? {})) {
    for (const via of entry.via ?? []) {
      if (typeof via !== "object" || !Blocking.has(via.severity)) {
        continue;
      }
      const id = via.url?.split("/").pop() ?? `${via.name}@${via.range}`;
      const key = `${id} ${via.name}`;
      const fixAvailable = Boolean(
        report.vulnerabilities[via.name]?.fixAvailable ?? entry.fixAvailable,
      );
      found.set(key, {
        id,
        package: via.name,
        severity: via.severity,
        title: via.title,
        fixAvailable,
        affects: name,
      });
    }
  }
  return [...found.values()];
}

/** What blocks the folder: advisories with no live exception, and exceptions that no longer hold. */
export function judge(folder, advisories, exceptions, today) {
  const problems = [];
  for (const advisory of advisories) {
    const exception = exceptions.find(
      (e) => e.folder === folder && e.advisory === advisory.id,
    );
    if (!exception) {
      problems.push(
        `${advisory.id} (${advisory.severity}, ${advisory.package}): ${advisory.title}`,
      );
    } else if (exception.expires < today) {
      problems.push(
        `${advisory.id}: its exception expired on ${exception.expires}; look again and renew or remove it`,
      );
    } else if (advisory.fixAvailable) {
      problems.push(
        `${advisory.id}: npm now offers a fix, so take it and remove the exception`,
      );
    }
  }
  return problems;
}

function main(folders) {
  const root = join(fileURLToPath(import.meta.url), "..", "..");
  const exceptions = JSON.parse(
    readFileSync(join(root, "scripts", "npm-audit-exceptions.json"), "utf8"),
  ).exceptions;
  const today = new Date().toISOString().slice(0, 10);
  let failed = false;
  for (const folder of folders) {
    let output;
    try {
      output = execSync("npm audit --omit=dev --json", {
        cwd: join(root, folder),
        encoding: "utf8",
      });
    } catch (error) {
      // npm audit exits non-zero when it finds anything; the report is still on stdout.
      output = error.stdout;
    }
    const problems = judge(
      folder,
      blockingAdvisories(JSON.parse(output)),
      exceptions,
      today,
    );
    const excused = exceptions
      .filter((e) => e.folder === folder)
      .map((e) => e.advisory);
    console.log(
      `${folder}: ${problems.length === 0 ? "nothing blocking" : `${problems.length} blocking`}${excused.length ? ` (excused: ${excused.join(", ")})` : ""}`,
    );
    for (const problem of problems) {
      console.log(`  ${problem}`);
    }
    failed ||= problems.length > 0;
  }
  process.exitCode = failed ? 1 : 0;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  main(process.argv.slice(2));
}
