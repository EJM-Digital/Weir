#!/usr/bin/env node
// NuGet vulnerability gate for the .NET server (and any other solution or project passed in).
//
//   node scripts/check-dotnet-vulnerabilities.mjs apps/server/Weir.slnx [more projects...]
//
// Runs `dotnet list <target> package --vulnerable --include-transitive --format json` against the
// NuGet advisory database and fails when any direct or transitive package has a High or Critical
// advisory. Lower severities are printed but do not fail, matching `npm audit --audit-level=high`
// for the web app. The target must already be restored (a build does that).

import { execFileSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const FAILING = new Set(["high", "critical"]);

/** The JSON report of `dotnet list package --vulnerable` for a target; `execute` is execFileSync, which a test replaces (the real one asks NuGet). */
export function dotnetList(target, execute = execFileSync) {
  return execute("dotnet", ["list", target, "package", "--vulnerable", "--include-transitive", "--format", "json"], {
    cwd: REPO,
    encoding: "utf8",
    maxBuffer: 64 * 1024 * 1024,
  });
}

/**
 * Scans each target; returns the exit code (0 clean, 1 a High or Critical advisory or a scan error, 2 the scan itself could not run).
 * `list` is how a target's JSON report is read, `dotnet list package` unless a test says otherwise.
 */
export function scan(targets, { list = dotnetList, repo = REPO, out = { log: console.log, error: console.error } } = {}) {
  let failing = 0;
  let reported = 0;
  for (const target of targets) {
    let raw;
    try {
      raw = list(target);
    } catch (err) {
      out.error(`[dotnet-vulnerabilities] dotnet list failed for ${target}:`);
      out.error(err.stdout ?? "");
      out.error(err.stderr ?? err.message);
      return 2;
    }
    const report = JSON.parse(raw);
    for (const problem of report.problems ?? []) {
      // A source that could not be reached means the scan did not happen; do not pass silently.
      out.error(`[dotnet-vulnerabilities] ${target}: ${problem.level ?? "problem"}: ${problem.text}`);
      if ((problem.level ?? "").toLowerCase() === "error") failing += 1;
    }
    for (const project of report.projects ?? []) {
      for (const framework of project.frameworks ?? []) {
        const packages = [...(framework.topLevelPackages ?? []), ...(framework.transitivePackages ?? [])];
        for (const pkg of packages) {
          for (const vulnerability of pkg.vulnerabilities ?? []) {
            const severity = String(vulnerability.severity ?? "unknown").toLowerCase();
            const line = `${path.relative(repo, project.path)} (${framework.framework}): ${pkg.id} ${pkg.resolvedVersion} - ${severity} - ${vulnerability.advisoryurl}`;
            reported += 1;
            if (FAILING.has(severity)) {
              failing += 1;
              out.error(`[dotnet-vulnerabilities] FAIL ${line}`);
            } else {
              out.log(`[dotnet-vulnerabilities] note ${line}`);
            }
          }
        }
      }
    }
  }

  if (failing) {
    out.error(`[dotnet-vulnerabilities] ${failing} High/Critical advisory finding(s) or scan error(s).`);
    return 1;
  }
  out.log(`[dotnet-vulnerabilities] No High or Critical advisories (${reported} lower-severity finding(s)).`);
  return 0;
}

/** The scan over the command line's targets; returns the exit code. `scanTargets` is `scan`, which a test replaces. */
export function main(targets, { scanTargets = scan, error = console.error } = {}) {
  if (targets.length === 0) {
    error("usage: node scripts/check-dotnet-vulnerabilities.mjs <solution-or-project> [...]");
    return 2;
  }
  return scanTargets(targets);
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) process.exitCode = main(process.argv.slice(2));
