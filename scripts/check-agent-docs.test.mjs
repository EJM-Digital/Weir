// node --test scripts/check-agent-docs.test.mjs
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { candidatesFor, checkDocs, main, proseOnly, requiredFiles } from "./check-agent-docs.mjs";

const script = path.join(path.dirname(fileURLToPath(import.meta.url)), "check-agent-docs.mjs");

/** A repository folder holding every required file, plus whatever `files` adds or replaces. */
function withRepository(files, callback) {
  const root = mkdtempSync(path.join(tmpdir(), "weir-agent-docs-"));
  try {
    const all = { "README.md": "# Weir\n", ...Object.fromEntries(requiredFiles.map((file) => [file, "# Page\n"])), ...files };
    for (const [file, text] of Object.entries(all)) {
      if (text === null) continue;
      mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
      writeFileSync(path.join(root, file), text);
    }
    callback(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("a repository with every required file and working links passes, and counts its Markdown files", () => {
  withRepository({ "docs/guide.md": "See [the agent file](../AGENTS.md#top) and [the web](https://example.com/x).\n" }, (root) => {
    const result = checkDocs(root);
    assert.deepEqual(result.failures, []);
    assert.equal(result.checked, requiredFiles.length + 2);
  });
});

test("a required file that is missing is named", () => {
  withRepository({ "docs/agent-harness.md": null }, (root) => {
    assert.deepEqual(checkDocs(root).failures, ["Missing required agent documentation file: docs/agent-harness.md"]);
  });
});

test("the README may not carry the retired Windows updater guidance", () => {
  withRepository({ "README.md": "This page predates the updater service.\n" }, (root) => {
    const { failures } = checkDocs(root);
    assert.equal(failures.length, 1);
    assert.match(failures[0], /obsolete pre-v2\.3 Windows updater guidance: predates the updater service/);
  });
  withRepository({ "README.md": "Run the Weir Updater` service now.\n" }, (root) => {
    assert.match(checkDocs(root).failures[0], /Weir Updater` service/);
  });
});

test("a relative link to a file that does not exist is named with its page", () => {
  withRepository({ "docs/guide.md": "Read [this](./missing.md) and ![a picture](<pics/none.png> \"title\").\n" }, (root) => {
    assert.deepEqual(checkDocs(root).failures, [
      "docs/guide.md links to missing local target: ./missing.md",
      "docs/guide.md links to missing local target: pics/none.png",
    ]);
  });
});

test("anchors, web addresses, mail links, empty links and links inside code are not checked", () => {
  const text = ["[a](#section) [b](https://example.com/none) [c](mailto:a@example.com) [d](?query) [e]()", "`[f](./nope.md)`", "```", "[g](./nope.md)", "```", "~~~", "[h](./nope.md)", "~~~"].join("\n");
  withRepository({ "docs/guide.md": text }, (root) => {
    assert.deepEqual(checkDocs(root).failures, []);
  });
});

test("a link with an anchor or a query is checked for the file it names, and a path with spaces is decoded", () => {
  withRepository({ "docs/a b.md": "# Spaced\n", "docs/guide.md": "[x](a%20b.md#part) [y](a%20b.md?raw=1) [z](gone.md#part)\n" }, (root) => {
    assert.deepEqual(checkDocs(root).failures, ["docs/guide.md links to missing local target: gone.md#part"]);
  });
});

test("a link from outside docs-site that starts with a slash is read from the repository root", () => {
  withRepository({ "docs/guide.md": "[ok](/AGENTS.md) [no](/nowhere.md)\n" }, (root) => {
    assert.deepEqual(checkDocs(root).failures, ["docs/guide.md links to missing local target: /nowhere.md"]);
  });
});

test("a docs-site page may link without an extension, to an index page, and by the site's own base path", () => {
  withRepository(
    {
      "docs-site/docs/intro.md": "[a](setup) [b](folder) [c](/Weir/img/logo.png) [d](/docs/setup) [e](/img/logo.png) [f](/Weir/img/none.png) [g](setup.mdx)\n",
      "docs-site/docs/setup.md": "# Setup\n",
      "docs-site/docs/folder/index.md": "# Folder\n",
      "docs-site/static/img/logo.png": "png",
    },
    (root) => {
      assert.deepEqual(checkDocs(root).failures, [
        "docs-site/docs/intro.md links to missing local target: /Weir/img/none.png",
        "docs-site/docs/intro.md links to missing local target: setup.mdx",
      ]);
    },
  );
});

test("a link that climbs out of the repository is never found", () => {
  withRepository({ "docs/guide.md": "[out](../../elsewhere.md)\n" }, (root) => {
    assert.deepEqual(checkDocs(root).failures, ["docs/guide.md links to missing local target: ../../elsewhere.md"]);
  });
});

test("a current page may not name a screen Weir no longer has, and says which line", () => {
  const text = ["Fine line.", "Open Settings > General to change it.", "Open Settings -> Upgrade to update.", "See the History page for runs.", "It was in Housekeeping."].join("\n");
  withRepository({ "docs/guide.md": text }, (root) => {
    assert.deepEqual(checkDocs(root).failures, [
      'docs/guide.md:3 names retired UI "Settings › Upgrade" as if it were current',
      'docs/guide.md:4 names retired UI "History page (now Activity)" as if it were current',
      'docs/guide.md:5 names retired UI "Housekeeping" as if it were current',
    ]);
  });
});

test("a retired name is allowed on a line that says so, in code, and in pages that record the past", () => {
  withRepository(
    {
      "docs/guide.md": "Housekeeping was removed. <!-- retired-ui: history -->\n`Housekeeping` in code\n",
      "docs/adr/0001.md": "Housekeeping existed.\n",
      "docs/archive/old.md": "Settings › General\n",
      "docs/release-notes/1.0.md": "Display density.\n",
      "CHANGELOG.md": "Home screen\n",
    },
    (root) => {
      assert.deepEqual(checkDocs(root).failures, []);
    },
  );
});

test("hidden folders and node_modules are not read, and a documentation folder that is absent is fine", () => {
  withRepository({ "docs/.hidden/page.md": "[x](./nope.md)\n", "docs/node_modules/pkg/readme.md": "[x](./nope.md)\n" }, (root) => {
    assert.deepEqual(checkDocs(root).failures, []);
  });
});

test("prose keeps links and drops code", () => {
  assert.equal(proseOnly("a `b` c\n```js\nx\n```\nd"), "a  c\n\nd");
});

test("a docs-site candidate list covers the page, its extensions and its index", () => {
  const root = path.join(path.sep, "repo");
  const list = candidatesFor(root, "docs-site/docs/a.md", path.join(root, "docs-site", "docs"), "setup");
  assert.deepEqual(list.map((candidate) => path.relative(root, candidate).split(path.sep).join("/")), [
    "docs-site/docs/setup",
    "docs-site/docs/setup.md",
    "docs-site/docs/setup.mdx",
    "docs-site/docs/setup/index.md",
  ]);
});

test("the report says how many problems there were and returns the exit code", () => {
  withRepository({ "docs/guide.md": "[x](./nope.md)\n" }, (root) => {
    const lines = [];
    assert.equal(main(root, (line) => lines.push(line)), 1);
    assert.deepEqual(lines, ["[agent-docs] docs/guide.md links to missing local target: ./nope.md", "[agent-docs] Failed with 1 documentation issue(s)."]);
  });
  withRepository({}, (root) => {
    const lines = [];
    assert.equal(main(root, (line) => lines.push(line)), 0);
    assert.match(lines[0], /Agent documentation map is valid \(8 Markdown files checked\)/);
  });
});

test("run as a command it checks this repository and exits 0", () => {
  const result = spawnSync(process.execPath, [script], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stderr, /Agent documentation map is valid/);
});
