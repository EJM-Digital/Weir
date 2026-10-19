#!/usr/bin/env node
// Release gate for the two kinds of CI that do not run on every pull request (docs/ci-cost.md): refuse to publish a tag
// unless each has passed on the tagged commit, or on an earlier commit that cannot differ from it where it matters.
//
//   windows   the Windows legs of the contract suite and the Windows scenario shards. ci.yml runs them on every push to
//             main that touches server code, nightly, and by hand; on a pull request only when it touches a Windows path.
//             The proof is ci.yml's `ci-passed` job on a push, schedule or manual run, whose step "The Windows legs ran and
//             passed" succeeds only when both were due and passed. A pass counts for the tag when its commit is the tagged
//             one or an ancestor whose shipped files are the same (scripts/release/shipped-paths.mjs, the rule the
//             golden-path gate uses).
//   mutation  the Mutation workflow (.github/workflows/mutation.yml), which never runs on a pull request. The proof is a
//             scheduled or manual run whose `check` job held every score to its floor. A pass counts for the tag when its
//             commit has the same shipped files and the same code and tests under apps/ and the same mutation setup, because
//             a changed test can lower a score without changing a shipped file.
//
// A pull_request run never counts: it tested a branch merged into its base, not this tree. The newest pass wins; a failed
// or cancelled run is not a pass and does not withdraw an older one on identical code, because the code it judged is the
// same. The checkout needs the full history (fetch-depth: 0).
//
// Usage (in release.yml):  node scripts/verify-heavy-ci-for-release.mjs <windows|mutation>
//   env GH_TOKEN (actions: read), GITHUB_REPOSITORY, GITHUB_REF_NAME (the tag), GITHUB_SHA (else `git rev-parse HEAD`)
// Options: --sha <sha>
import { execFileSync } from "node:child_process";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

import { changedPaths, sameShippedFiles } from "./release/shipped-paths.mjs";
import { REQUIRED_EVIDENCE, evaluateRun, gh, runJobs } from "./verify-ci-for-release.mjs";
import { MAX_CARRY_COMMITS } from "./verify-golden-path-for-release.mjs";

// Everything a mutation score depends on besides the shipped files: the tests and the Stryker setup under apps/, and the
// files that choose the targets, hold the floors and run the check.
const MUTATION_INPUTS = [
  "apps/",
  ".github/mutation-targets.json",
  ".github/workflows/mutation.yml",
  "docs/coverage/mutation-floors.json",
  "scripts/check-mutation-floors.mjs",
  "scripts/select-mutation-targets.mjs",
];

export function isMutationInput(path) {
  const file = String(path).replaceAll("\\", "/").replace(/^\.\//, "");
  return MUTATION_INPUTS.some((entry) => (entry.endsWith("/") ? file.startsWith(entry) : file === entry));
}

// Whether a mutation run on `base` counts for `head`: the same rule as the shipped files, plus no change to a mutation input.
export function sameMutatedCode(base, head, git) {
  const same = sameShippedFiles(base, head, git);
  if (!same.ok || base === head) return same;
  const inputs = changedPaths(base, head, git).filter(isMutationInput);
  if (inputs.length === 0) return same;
  const named = inputs.slice(0, 5).join(", ") + (inputs.length > 5 ? `, and ${inputs.length - 5} more` : "");
  return { ok: false, reason: `${inputs.length} file${inputs.length === 1 ? "" : "s"} the mutation run reads changed: ${named}`, shipped: same.shipped };
}

export const PROOFS = {
  windows: {
    label: "the Windows contract legs and the Windows scenario shards",
    workflow: "ci.yml",
    events: ["push", "schedule", "workflow_dispatch"],
    // The workflow's own verdict, plus the step that succeeds only when the Windows legs were due and passed.
    evidence: [{ job: REQUIRED_EVIDENCE[0].job, steps: [...REQUIRED_EVIDENCE[0].steps, "The Windows legs ran and passed"] }],
    sameCode: sameShippedFiles,
    advice: "Run the full CI on the tag (a manual run skips nothing), or wait for the nightly run when the tag is the newest commit on main:",
  },
  mutation: {
    label: "the mutation scores against their floors",
    workflow: "mutation.yml",
    events: ["schedule", "workflow_dispatch"],
    evidence: [{ job: "check", steps: ["Mutation scores against the floors"] }],
    sameCode: sameMutatedCode,
    advice: "Run the Mutation workflow on the tag (it takes hours) and wait for it to pass:",
  },
};

// Finds the run that proves `sha`: its own, else the nearest earlier commit's that qualifies and whose code is the same.
// runsOf(commit) lists that commit's runs of the proof's workflow; jobsOf(run) lists a run's jobs; git(args) runs git.
// Returns { ok: true, sha, run, carried, reason } or { ok: false, problems }.
export function findProof({ proof, sha, runsOf, jobsOf, git, maxCommits = MAX_CARRY_COMMITS }) {
  const problems = [];
  let earlier = [];
  try {
    earlier = git(["rev-list", `--max-count=${maxCommits + 1}`, sha]).split("\n").map((line) => line.trim()).filter(Boolean).slice(1);
  } catch (error) {
    problems.push(`the commits before ${sha} could not be read (${String(error.message).split("\n")[0]}); fetch the full history`);
  }
  let stoppedAt = null;
  for (const candidate of [sha, ...earlier]) {
    const runs = runsOf(candidate).filter((run) => run.status === "completed" && proof.events.includes(run.event));
    if (runs.length === 0) continue;
    let same = { ok: true, reason: "it is the tagged commit itself" };
    if (candidate !== sha) {
      same = proof.sameCode(candidate, sha, git);
      if (!same.ok) {
        stoppedAt ??= `${candidate}: ${same.reason}`;
        continue;
      }
    }
    for (const run of runs) {
      const why = run.conclusion === "success" ? evaluateRun(run, jobsOf(run), proof.evidence, proof.events) : [`concluded ${run.conclusion}`];
      if (why.length === 0) return { ok: true, sha: candidate, run, carried: candidate !== sha, reason: same.reason };
      problems.push(`run ${run.databaseId} (${run.event}, ${run.url}) on ${candidate} does not qualify: ${why.join("; ")}`);
    }
  }
  if (stoppedAt) problems.push(`an earlier commit with a run was not the same code (${stoppedAt})`);
  return { ok: false, problems };
}

const runGit = (args) => execFileSync("git", args, { encoding: "utf8", maxBuffer: 256 * 1024 * 1024, stdio: ["ignore", "pipe", "pipe"] });

// Every completed run of the workflow for the events, newest first, grouped by commit. Two pages of 100 per event cover the
// commits a tag looks back over.
function runIndex(repository, proof) {
  const bySha = new Map();
  for (const event of proof.events) {
    for (const page of [1, 2]) {
      const listing = gh(["api", `repos/${repository}/actions/workflows/${proof.workflow}/runs?event=${event}&status=completed&per_page=100&page=${page}`]);
      for (const run of listing.workflow_runs) {
        const entry = { databaseId: run.id, event: run.event, status: run.status, conclusion: run.conclusion, url: run.html_url };
        bySha.set(run.head_sha, [...(bySha.get(run.head_sha) ?? []), entry]);
      }
      if (listing.workflow_runs.length < 100) break;
    }
  }
  return bySha;
}

// Judges one proof for one commit and says why. Returns true when the release may go ahead.
export function runGate({ name, tag, repository, sha, runsOf, jobsOf, git = runGit, log = console.log, error = console.error }) {
  const proof = PROOFS[name];
  if (!proof) throw new Error(`Unknown proof "${name}"; use ${Object.keys(PROOFS).join(" or ")}.`);
  if (!repository) throw new Error("GITHUB_REPOSITORY must be set.");
  if (!/^[0-9a-f]{40}$/.test(sha ?? "")) throw new Error(`Not a full commit SHA: ${sha}`);
  log(`Release gate: ${proof.label} must already have passed (${proof.workflow}) on ${sha} or on an earlier commit with the same code.`);

  const found = findProof({ proof, sha, runsOf, jobsOf, git });
  if (found.ok) {
    log(`PASS: ${proof.workflow} run ${found.run.databaseId} (${found.run.event}, ${found.run.url}) passed on ${found.sha}.`);
    if (found.carried) log(`  ${found.sha} is an earlier commit than ${sha}: ${found.reason}, so that pass is reused for this tag.`);
    return true;
  }
  error(`::error::No ${proof.workflow} run proves ${proof.label} on ${sha}. The release will not publish.`);
  if (found.problems.length === 0) error(`  - none of the ${MAX_CARRY_COMMITS} commits up to it has a push, scheduled or manual run of ${proof.workflow}.`);
  for (const problem of found.problems) error(`  - ${problem}`);
  error(proof.advice);
  error(`  gh workflow run ${proof.workflow} --repo ${repository} --ref ${tag || "<tag>"}`);
  error("then re-run this release.");
  return false;
}

function main() {
  const argv = process.argv.slice(2);
  const option = (flag) => (argv.includes(flag) ? argv[argv.indexOf(flag) + 1] : undefined);
  const name = argv.find((arg) => PROOFS[arg]);
  if (!name) {
    console.error(`Usage: node scripts/verify-heavy-ci-for-release.mjs <${Object.keys(PROOFS).join("|")}> [--sha <sha>]`);
    process.exit(2);
  }
  const repository = process.env.GITHUB_REPOSITORY;
  const sha = option("--sha") || process.env.GITHUB_SHA || execFileSync("git", ["rev-parse", "HEAD^{commit}"], { encoding: "utf8" }).trim();
  const index = runIndex(repository, PROOFS[name]);
  const passed = runGate({
    name,
    tag: process.env.GITHUB_REF_NAME,
    repository,
    sha,
    runsOf: (commit) => index.get(commit) ?? [],
    jobsOf: (run) => runJobs(repository, run.databaseId),
  });
  if (!passed) process.exit(1);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    main();
  } catch (error) {
    console.error(`::error::${error.message}`);
    process.exit(1);
  }
}
