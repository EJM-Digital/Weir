// The part of scripts/merge-web-coverage.mjs that needs the web app's coverage packages, installed with `npm ci` in apps/web.
// The repository checks do not install them, so this file is listed in docs/coverage/exclusions.json; the coverage job runs it.
import { createRequire } from "node:module";
import { mkdirSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

// The coverage libraries are the web app's packages, installed with `npm ci` in apps/web; they are loaded when a report is made, not for merging alone.
const webRequire = createRequire(path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "apps", "web", "package.json"));
const lazy = (name) => () => webRequire(name);
const libCoverage = lazy("istanbul-lib-coverage");
const libReport = lazy("istanbul-lib-report");
const libSourceMaps = lazy("istanbul-lib-source-maps");
const reports = lazy("istanbul-reports");

/** The instrumented build's coverage, with every position moved from the compiled code back to the source. */
async function toSource(instrumented, root, relativeKey) {
  const absolute = path.resolve(root);
  const map = libCoverage().createCoverageMap({});
  for (const [key, coverage] of Object.entries(instrumented)) {
    map.addFileCoverage({ ...coverage, path: path.join(absolute, key) });
  }
  const remapped = await libSourceMaps().createSourceMapStore().transformCoverage(map);
  const sourced = {};
  for (const file of remapped.files()) {
    const key = relativeKey(file, root);
    if (key !== undefined) sourced[key] = structuredClone({ ...remapped.fileCoverageFor(file).toJSON(), path: key });
  }
  return sourced;
}

function writeReports(merged, root, out) {
  const absolute = path.resolve(root);
  const map = libCoverage().createCoverageMap({});
  for (const [key, coverage] of Object.entries(merged)) {
    map.addFileCoverage({ ...coverage, path: path.join(absolute, key) });
  }
  mkdirSync(out, { recursive: true });
  const context = libReport().createContext({ dir: out, coverageMap: map });
  for (const name of ["json", "json-summary", "lcovonly"]) {
    reports().create(name).execute(context);
  }
  const summary = map.getCoverageSummary();
  return { lines: summary.lines, branches: summary.branches };
}

export const reporting = { toSource, writeReports };
