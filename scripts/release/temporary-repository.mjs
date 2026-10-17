// A throwaway git repository for tests that need real commits (the release scripts judge real history).
// Every repository made here is removed when the process exits.
import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";

const roots = [];
process.on("exit", () => {
  for (const root of roots) rmSync(root, { recursive: true, force: true });
});

// Returns { git, commit }: git(args) runs git inside the repository (stdout back, throws on a non-zero exit), and
// commit(files, message) writes the files ({ path: content }), commits them and returns the new commit's full SHA.
export function repository() {
  const root = mkdtempSync(join(tmpdir(), "weir-release-"));
  roots.push(root);
  const git = (args) =>
    execFileSync(
      "git",
      ["-C", root, "-c", "user.name=test", "-c", "user.email=test@example.test", "-c", "commit.gpgsign=false", ...args],
      { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] },
    );
  git(["init", "-q", "-b", "main"]);
  const commit = (files, message) => {
    for (const [path, content] of Object.entries(files)) {
      mkdirSync(dirname(join(root, path)), { recursive: true });
      writeFileSync(join(root, path), content);
    }
    git(["add", "-A"]);
    git(["commit", "-q", "-m", message]);
    return git(["rev-parse", "HEAD"]).trim();
  };
  return { git, commit };
}
