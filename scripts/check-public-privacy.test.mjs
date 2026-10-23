// node --test scripts/check-public-privacy.test.mjs
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { publicFiles, runAudit, runCheck, scanText, sha256 } from "./check-public-privacy.mjs";

const kinds = (text, hashes = []) => scanText(text, hashes).map((hit) => hit.kind);

test("private IPv4 addresses are found in all three private ranges", () => {
  assert.deepEqual(kinds("open http://10.4.5.6:9347/ now"), ["privateIp"]);
  assert.deepEqual(kinds("host 172.16.0.9 and 172.31.255.1"), ["privateIp", "privateIp"]);
  assert.deepEqual(kinds("at 192.168.0.12"), ["privateIp"]);
});

test("documentation addresses, public addresses and other ranges are fine", () => {
  assert.deepEqual(kinds("192.0.2.10, 198.51.100.7 and 203.0.113.250"), []);
  assert.deepEqual(kinds("172.15.0.1 and 172.32.0.1 are not private; 8.8.8.8 is public"), []);
  assert.deepEqual(kinds("127.0.0.1 and 0.0.0.0"), []);
});

test("a version number or a longer dotted number is not an address", () => {
  assert.deepEqual(kinds("version 1.10.4.5 and build 10.0.26300.1234"), []);
});

test("a private range written as CIDR with a zero host is allowed, a machine inside one is not", () => {
  assert.deepEqual(kinds("trust 10.0.0.0/8, 172.16.0.0/12 and 192.168.0.0/16"), []);
  assert.deepEqual(kinds("trust 10.0.0.5/24"), ["privateIp"]);
});

test("Windows default machine names are found", () => {
  assert.deepEqual(kinds("on WIN-AB12CD34EF56 today"), ["machineName"]);
  assert.deepEqual(kinds("WIN-SHORT and Windows-Server are fine"), []);
});

test("UNC paths are found, placeholders are not", () => {
  assert.deepEqual(kinds(String.raw`share \\storage\media\tv`), ["uncPath"]);
  assert.deepEqual(kinds(String.raw`share "\\\\storage\\media"`), ["uncPath"]);
  assert.deepEqual(kinds(String.raw`share \\<nas>\<share>\tv and \\<host>\media`), []);
});

test("an escaped drive path or a unicode escape is not a UNC path", () => {
  assert.deepEqual(kinds(String.raw`"C:\\Data\\Media" and "caf\\u00e9\\"`), []);
});

test("email addresses are found, noreply and example ones are not", () => {
  assert.deepEqual(kinds("write to jane.doe@mail.test"), ["email"]);
  assert.deepEqual(kinds("noreply@anthropic.com, 123+jane@users.noreply.github.com, me@example.com, a@sub.example.org"), []);
});

test("api key values are found, placeholders are not", () => {
  assert.deepEqual(kinds("?api_key=a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6"), ["apiKey"]);
  assert.deepEqual(kinds('{"apikey": "9f8e7d6c5b4a39281716"}'), ["apiKey"]);
  assert.deepEqual(kinds("api_key=<your-key>, apikey=YOUR_API_KEY_HERE, api_key=xxxxxxxxxxxxxxxx, api_key=changeme-changeme"), []);
  assert.deepEqual(kinds("the X-Api-Key header and api_key=header_name_only"), []);
});

test("a known private word is found by its hash, in any case and inside a longer name", () => {
  const hashes = [sha256("lab-box"), sha256("10.9.9.")];
  assert.deepEqual(kinds("connect to LAB-BOX now", hashes), ["knownLiteral"]);
  assert.deepEqual(kinds("name: lab-box.local", hashes), ["knownLiteral"]);
  assert.deepEqual(kinds("my-lab-box-2", hashes), ["knownLiteral"]);
  assert.deepEqual(kinds("reach 10.9.9.40", hashes).sort(), ["knownLiteral", "privateIp"]);
  assert.deepEqual(kinds("a laboratory box and 10.9.8.1", hashes), ["privateIp"]);
});

test("a hit names its line and never the matched text", () => {
  const hits = scanText("fine\nfine\nat 192.168.7.7\n", []);
  assert.deepEqual(hits, [{ kind: "privateIp", line: 3 }]);
});

test("the shipped hash list holds exactly three SHA-256 hashes and no readable words", () => {
  const { sha256: list } = JSON.parse(readFileSync(new URL("./public-privacy-hashes.json", import.meta.url), "utf8"));
  assert.equal(list.length, 3);
  for (const entry of list) assert.match(entry, /^[0-9a-f]{64}$/);
});

// The files a visitor can read, the check over them and the audit over everything tracked, in a folder laid out like the repository.
function withRepository(files, callback) {
  const root = mkdtempSync(path.join(tmpdir(), "weir-privacy-"));
  try {
    for (const [file, text] of Object.entries(files)) {
      mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
      writeFileSync(path.join(root, file), text);
    }
    callback(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

const capture = () => {
  const seen = { log: [], error: [] };
  return { seen, out: { log: (line) => seen.log.push(line), error: (line) => seen.error.push(line) } };
};

test("the public files are the root pages, the documentation, the docs site, the docker pages and the built web app, and not the archive or plans", () => {
  withRepository(
    {
      "README.md": "",
      "notes.txt": "",
      "docs/guide.md": "",
      "docs/guide.txt": "",
      "docs/archive/old.md": "",
      "docs/exec-plans/plan.md": "",
      "docs-site/docs/page.mdx": "",
      "docs-site/docs/data.json": "",
      "docs-site/docs/node_modules/x.md": "",
      "docs-site/docs/build/x.md": "",
      "docs-site/docs/.docusaurus/x.md": "",
      "docker/README.md": "",
      "apps/web/dist/index.html": "",
      "apps/web/dist/logo.png": "",
    },
    (root) => {
      assert.deepEqual(
        publicFiles(root).map((file) => path.relative(root, file).split(path.sep).join("/")).sort(),
        ["README.md", "apps/web/dist/index.html", "docker/README.md", "docs-site/docs/data.json", "docs-site/docs/page.mdx", "docs/guide.md"],
      );
    },
  );
});

test("a public file with something private in it fails the check, naming the file and line but not the value", () => {
  withRepository({ "README.md": "fine\nat 192.168.7.7\n", "docs/binary.md": "a\u0000b 10.1.2.3", "docs/ok.md": "nothing" }, (root) => {
    const { seen, out } = capture();
    assert.equal(runCheck(root, out), 1);
    assert.equal(seen.error[0], "README.md:2  private IP address");
    assert.match(seen.error[1], /1 private-looking value\(s\) in public files\./);
    assert.ok(!seen.error.join("\n").includes("192.168"));
    assert.deepEqual(seen.log, ["The web bundle is not built here, so it was not checked."]);
  });
});

test("public files with nothing private pass, and say how many were read", () => {
  withRepository({ "README.md": "nothing", "apps/web/dist/app.js": "x" }, (root) => {
    const { seen, out } = capture();
    assert.equal(runCheck(root, out), 0);
    assert.deepEqual(seen.log, ["No private values in 2 public files."]);
  });
});

test("the audit lists, by kind, every tracked text file that holds a match, and skips pictures and missing files", () => {
  withRepository({ "src/a.cs": "var ip = \"10.0.0.5\";\n", "src/b.txt": "clean", "src/p.png": "10.0.0.6", "src/gone.txt": "x" }, (root) => {
    const run = (...args) => execFileSync("git", args, { cwd: root, stdio: "ignore" });
    run("init", "-q");
    run("add", "-A");
    rmSync(path.join(root, "src", "gone.txt"));
    const { seen, out } = capture();
    runAudit(root, out);
    const text = seen.log.join("\n");
    assert.match(text, /private IP address: 1\n {2}src\/a\.cs:1/);
    assert.match(text, /email address: 0/);
    assert.ok(!text.includes("p.png"));
  });
});

test("run as a command it checks this repository and exits 0", () => {
  const result = spawnSync(process.execPath, [fileURLToPath(new URL("./check-public-privacy.mjs", import.meta.url))], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /No private values in \d+ public files\./);
});

test("run with --audit it prints the audit", () => {
  const result = spawnSync(process.execPath, [fileURLToPath(new URL("./check-public-privacy.mjs", import.meta.url)), "--audit"], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /private IP address: \d+/);
});
