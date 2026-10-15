// Running commands for the smoke, which only ever runs on a Linux machine with Docker.
import { spawnSync } from "node:child_process";

/** Runs a shell command line and reports how it ended, whether or not it succeeded. `status` is null when `timeoutMs` killed it. */
export function runForStatus(command, { env = {}, timeoutMs } = {}) {
  const result = spawnSync("sh", ["-c", command], { encoding: "utf8", env: { ...process.env, ...env }, maxBuffer: 64 * 1024 * 1024, timeout: timeoutMs });
  return { status: result.status, output: `${result.stdout}${result.stderr}` };
}

/** Runs a shell command line and returns its output (stdout, then stderr). A non-zero exit throws, unless `allowFailure`. */
export function run(command, { env = {}, allowFailure = false } = {}) {
  const result = runForStatus(command, { env });
  if (result.status !== 0 && !allowFailure) {
    throw new Error(`${command} exited ${result.status}\n${result.output}`);
  }
  return result.output;
}

/** A word for `sh -c`: single-quoted, so nothing in it is expanded. */
export function shellQuote(word) {
  return `'${String(word).replaceAll("'", String.raw`'\''`)}'`;
}
