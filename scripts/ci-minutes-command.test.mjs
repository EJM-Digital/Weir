// node --test scripts/ci-minutes-command.test.mjs
// The CI minutes report as a command: asking GitHub again after a failed answer, leaving out a run whose jobs cannot be read, and the options.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { formatReport, ghJson, main, summarize } from "./ci-minutes.mjs";

const job = (name, labels, seconds) => ({
  name,
  labels,
  started_at: "2026-10-19T10:00:00Z",
  completed_at: new Date(Date.parse("2026-10-19T10:00:00Z") + seconds * 1000).toISOString(),
});

test("GitHub is asked again after a failed answer, twice, and the last failure says why", () => {
  const asked = [];
  const flaky = (args) => {
    asked.push(args);
    if (asked.length < 3) throw Object.assign(new Error("exit 1"), { stderr: "HTTP 502" });
    return '{"ok":true}';
  };
  assert.deepEqual(ghJson(["api", "x"], { gh: flaky }), { ok: true });
  assert.equal(asked.length, 3);

  const always = (args) => {
    throw Object.assign(new Error("exit 1"), { stderr: " HTTP 500\n" });
  };
  assert.throws(() => ghJson(["api", "x"], { gh: always }), /^Error: gh api x failed: HTTP 500$/);
  assert.throws(
    () => ghJson(["api", "y"], { gh: () => { throw new Error("no gh"); } }),
    /gh api y failed: no gh/,
  );
});

test("a workflow that billed the same in fewer minutes is listed after one that took longer, and then by name", () => {
  const runs = [
    { name: "B", event: "push", jobs: [job("one", ["windows-latest"], 60)] },
    { name: "A", event: "push", jobs: [job("one", ["ubuntu-latest"], 120)] },
    { name: "C", event: "push", jobs: [job("one", ["ubuntu-latest"], 120)] },
  ];
  assert.deepEqual(summarize(runs).workflows.map((row) => row.key), ["A", "C", "B"]);
});

test("a long job name is cut to fit its table, and the table lists a self-hosted runner apart", () => {
  const longName = `Workflow: ${"a very long job name ".repeat(5)}`;
  const text = formatReport(summarize([{ name: "W", event: "push", jobs: [job(longName.slice(10), ["self-hosted", "linux"], 60)] }]), { days: 1 });
  assert.match(text, /W: a very long job name a very long job name a very long jo\.\.\. {1,}0 {1,}0 {1,}1 {1,}0 {1,}1/);
});

/** GitHub as the report asks it: one page of runs, each with its jobs, and the runs named in `unreadable` answer with an error. */
function github({ runs, unreadable = [] }) {
  return (args) => {
    const route = args[1];
    if (/\/actions\/runs\?/.test(route)) return { workflow_runs: runs };
    const id = Number(/runs\/(\d+)\/jobs/.exec(route)[1]);
    if (unreadable.includes(id)) throw new Error(`gh could not read run ${id}`);
    return { jobs: [job("build", ["ubuntu-latest"], 90)] };
  };
}

const NOW = Date.parse("2026-10-23T05:00:00Z");

test("the report counts back from now, names the runs it could not read and how many requests it made", () => {
  const printed = [];
  const complaints = [];
  const runs = [{ id: 1, name: "CI", event: "push" }, { id: 2, name: "CI", event: "push" }, { id: 3, name: "Audit", event: "schedule" }];

  const code = main(["--days", "3", "--top", "1"], { api: github({ runs, unreadable: [2] }), now: () => NOW, log: (line) => printed.push(line), error: (line) => complaints.push(line) });

  assert.equal(code, 0);
  assert.match(printed[0], /^GitHub Actions minutes over the last 3 days: 2 runs, 4 minutes, 4 billed/);
  assert.deepEqual(complaints, ["gh could not read run 2", "1 run(s) could not be read and are not counted: 2", "(4 API requests)"]);
});

test("--json prints the summary with the window, --legs keeps the matrix legs apart, and a full list says it was cut", () => {
  const printed = [];
  const runs = [{ id: 1, name: "CI", event: "push" }];
  main(["--json", "--legs", "--days", "2", "--repo", "o/r"], { api: github({ runs }), now: () => NOW, log: (line) => printed.push(line), error: () => {} });
  const summary = JSON.parse(printed[0]);
  assert.deepEqual([summary.days, summary.since, summary.runs, summary.jobs[0].key], [2, "2026-10-21", 1, "CI: build"]);

  const cut = [];
  main(["--max-runs", "1"], { api: github({ runs }), now: () => NOW, log: (line) => cut.push(line), error: () => {} });
  assert.match(cut[0], /Only the newest 1 runs were read \(--max-runs\)/);
});

test("a window or a cap that is not a whole number of at least one is refused", () => {
  const api = () => assert.fail("nothing should be asked");
  for (const days of ["0", "1.5", "x"]) assert.throws(() => main(["--days", days], { api }), /--days must be a whole number of days, 1 or more\./);
  for (const cap of ["0", "-3"]) assert.throws(() => main(["--max-runs", cap], { api }), /--max-runs must be a whole number, 1 or more\./);
});

test("run as a command, a bad option is an error message and exit 1", () => {
  const script = fileURLToPath(new URL("./ci-minutes.mjs", import.meta.url));
  const result = spawnSync(process.execPath, [script, "--days", "0"], { encoding: "utf8" });
  assert.equal(result.status, 1);
  assert.equal(result.stderr.trim(), "--days must be a whole number of days, 1 or more.");
});
