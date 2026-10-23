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
//   mutation  the Mutation workflow (.github/workflows/mutation.yml), which never runs on a pull request. The proof is judged
//             per target (.github/mutation-targets.json): each needs a scheduled or manual run on the tagged commit, or on an
//             earlier commit, in which that target's job succeeded and the `check` job held the scores to their floors. An
//             earlier run counts for a target while the target's own inputs are unchanged between that commit and the tag:
//             the files it mutates, the tests that kill its mutants and its mutation setup (scripts/release/mutation-inputs.mjs).
//             A change to another target's files does not matter. Targets with no such run are listed with the command that
//             runs just those.
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
import { fileURLToPath, pathToFileURL } from "node:url";

import { changesToTarget, entryChanged } from "./release/mutation-inputs.mjs";
import { changedPaths, sameShippedFiles } from "./release/shipped-paths.mjs";
import { loadTargets } from "./select-mutation-targets.mjs";
import { REQUIRED_EVIDENCE, evaluateRun, gh, runJobs } from "./verify-ci-for-release.mjs";
import { MAX_CARRY_COMMITS } from "./verify-golden-path-for-release.mjs";

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
    perTarget: true,
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

const repositoryRoot = resolve(fileURLToPath(new URL("..", import.meta.url)));
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

// Finds, for each mutation target, the run that proves it on `sha`: the nearest commit (the tagged one first) with a
// scheduled or manual run in which the target's job and the `check` job succeeded, and whose target inputs are the same as
// at `sha`. Returns [{ target, ok, sha, run, carried, problems }] in the order of `targets`.
export function findTargetProofs({ proof, sha, targets, root, runsOf, jobsOf, git, maxCommits = MAX_CARRY_COMMITS }) {
  let earlier = [];
  const shared = [];
  try {
    earlier = git(["rev-list", `--max-count=${maxCommits + 1}`, sha]).split("\n").map((line) => line.trim()).filter(Boolean).slice(1);
  } catch (error) {
    shared.push(`the commits before ${sha} could not be read (${String(error.message).split("\n")[0]}); fetch the full history`);
  }
  const changes = new Map();
  const changesSince = (commit) => {
    if (!changes.has(commit)) {
      try {
        changes.set(commit, { paths: changedPaths(commit, sha, git) });
      } catch (error) {
        changes.set(commit, { unreadable: String(error.message).split("\n")[0] });
      }
    }
    return changes.get(commit);
  };
  const jobs = new Map();
  const jobsFor = (run) => {
    if (!jobs.has(run.databaseId)) jobs.set(run.databaseId, jobsOf(run));
    return jobs.get(run.databaseId);
  };
  return targets.map((target) => {
    const problems = [...shared];
    let stoppedAt = null;
    for (const candidate of [sha, ...earlier]) {
      const runs = runsOf(candidate).filter((run) => run.status === "completed" && proof.events.includes(run.event));
      if (runs.length === 0) continue;
      if (candidate !== sha) {
        const since = changesSince(candidate);
        const reasons = since.unreadable ? [`the changes since could not be read (${since.unreadable})`] : changesToTarget(target, since.paths, root);
        if (!since.unreadable && entryChanged(target, candidate, sha, git)) reasons.push(".github/mutation-targets.json (its entry)");
        if (reasons.length > 0) {
          const named = reasons.slice(0, 3).join(", ") + (reasons.length > 3 ? `, and ${reasons.length - 3} more` : "");
          stoppedAt ??= `${candidate}: ${named}`;
          continue;
        }
      }
      for (const run of runs) {
        const runJobs = run.conclusion === "success" ? jobsFor(run) : [];
        const why = run.conclusion === "success" ? evaluateRun(run, runJobs, proof.evidence, proof.events) : [`concluded ${run.conclusion}`];
        if (why.length === 0 && !runJobs.some((job) => job.name === `Mutate ${target.target}` && job.conclusion === "success")) {
          why.push(`it did not mutate ${target.target}`);
        }
        if (why.length === 0) return { target: target.target, ok: true, sha: candidate, run, carried: candidate !== sha, problems: [] };
        problems.push(`run ${run.databaseId} (${run.event}, ${run.url}) on ${candidate} does not qualify: ${why.join("; ")}`);
      }
    }
    if (stoppedAt) problems.push(`an earlier commit with a run changed this target's files, tests or setup (${stoppedAt})`);
    return { target: target.target, ok: false, problems };
  });
}

function gateMutation({ proof, tag, repository, sha, runsOf, jobsOf, git, log, error, targets, root }) {
  const results = findTargetProofs({ proof, sha, targets, root, runsOf, jobsOf, git });
  for (const result of results.filter((item) => item.ok)) {
    const reused = result.carried ? `; its files, tests and mutation setup are unchanged since` : "";
    log(`PASS ${result.target}: ${proof.workflow} run ${result.run.databaseId} (${result.run.event}, ${result.run.url}) passed on ${result.sha}${reused}.`);
  }
  const missing = results.filter((item) => !item.ok);
  if (missing.length === 0) return true;
  error(`::error::No ${proof.workflow} run proves ${proof.label} for ${missing.length} of ${results.length} targets on ${sha}: ${missing.map((item) => item.target).join(", ")}. The release will not publish.`);
  for (const item of missing) {
    error(`  ${item.target}:`);
    if (item.problems.length === 0) error(`    - none of the ${MAX_CARRY_COMMITS} commits up to the tag has a scheduled or manual run of ${proof.workflow}.`);
    for (const problem of item.problems) error(`    - ${problem}`);
  }
  error("Run just the missing targets on the tag (a few targets take minutes, not hours) and wait for it to pass:");
  error(`  gh workflow run ${proof.workflow} --repo ${repository} --ref ${tag || "<tag>"} -f targets=${missing.map((item) => item.target).join(",")}`);
  error("then re-run this release.");
  return false;
}

// Judges one proof for one commit and says why. Returns true when the release may go ahead.
export function runGate({ name, tag, repository, sha, runsOf, jobsOf, git = runGit, log = console.log, error = console.error, root = repositoryRoot, targets }) {
  const proof = PROOFS[name];
  if (!proof) throw new Error(`Unknown proof "${name}"; use ${Object.keys(PROOFS).join(" or ")}.`);
  if (!repository) throw new Error("GITHUB_REPOSITORY must be set.");
  if (!/^[0-9a-f]{40}$/.test(sha ?? "")) throw new Error(`Not a full commit SHA: ${sha}`);
  if (proof.perTarget) {
    log(`Release gate: ${proof.label} must already have passed (${proof.workflow}) for every target on ${sha}, or on an earlier commit where that target's own files, tests and setup are the same.`);
    return gateMutation({ proof, tag, repository, sha, runsOf, jobsOf, git, log, error, targets: targets ?? loadTargets(root), root });
  }
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
