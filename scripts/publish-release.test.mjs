// node --test scripts/publish-release.test.mjs
import assert from "node:assert/strict";
import { test } from "node:test";

import { missingFromDraft, publish } from "./publish-release.mjs";

const files = [
  {
    path: "windows-artifact/Weir-win-Setup.exe",
    name: "Weir-win-Setup.exe",
    size: 100,
  },
  { path: "weir-web-dist.zip", name: "weir-web-dist.zip", size: 50 },
];

/** A fake GitHub CLI holding one release; records every call. */
function fakeGh(release, { uploadDrops = [] } = {}) {
  const calls = [];
  const gh = (args) => {
    calls.push(args.join(" "));
    const [, verb] = args;
    if (verb === "view") {
      if (!release.exists) {
        const error = new Error("release not found");
        error.stderr = "release not found";
        throw error;
      }
      return JSON.stringify({
        isDraft: release.isDraft,
        assets: release.assets,
      });
    }
    if (verb === "create") {
      Object.assign(release, { exists: true, isDraft: true, assets: [] });
    } else if (verb === "upload") {
      for (const path of args.slice(4)) {
        const file = files.find((f) => f.path === path);
        if (!uploadDrops.includes(file.name)) {
          release.assets = [
            ...release.assets.filter((a) => a.name !== file.name),
            { name: file.name, size: file.size },
          ];
        }
      }
    } else if (verb === "edit") {
      release.isDraft = false;
    }
    return "";
  };
  return { gh, calls };
}

test("a new release is made as a draft, filled, checked and only then published", () => {
  const release = { exists: false };
  const { gh, calls } = fakeGh(release);
  publish({
    tag: "v1.0.0-rc.18",
    notesFile: "notes.md",
    prerelease: true,
    files,
    gh,
  });
  const verbs = calls.map((c) => c.split(" ")[1]);
  assert.deepEqual(
    verbs.filter((v) => v !== "view"),
    ["create", "upload", "edit"],
  );
  assert.match(
    calls.find((c) => c.includes(" create ")),
    /--draft .*--prerelease/,
  );
  assert.match(calls.at(-1), /--draft=false --prerelease/);
  assert.equal(release.isDraft, false);
});

test("a draft missing a file after the upload is never published", () => {
  const release = { exists: false };
  const { gh, calls } = fakeGh(release, { uploadDrops: ["weir-web-dist.zip"] });
  assert.throws(
    () =>
      publish({
        tag: "v1.0.0-rc.18",
        notesFile: "notes.md",
        prerelease: true,
        files,
        gh,
      }),
    /still missing weir-web-dist\.zip/,
  );
  assert.equal(release.isDraft, true);
  assert.ok(!calls.some((c) => c.includes(" edit ")));
});

test("a draft left by a failed run is finished, attaching only what it lacks", () => {
  const release = {
    exists: true,
    isDraft: true,
    assets: [{ name: "Weir-win-Setup.exe", size: 100 }],
  };
  const { gh, calls } = fakeGh(release);
  publish({
    tag: "v1.0.0-rc.18",
    notesFile: "notes.md",
    prerelease: true,
    files,
    gh,
  });
  assert.ok(!calls.some((c) => c.includes(" create ")));
  assert.equal(
    calls.find((c) => c.includes(" upload ")),
    "release upload v1.0.0-rc.18 --clobber weir-web-dist.zip",
  );
  assert.equal(release.isDraft, false);
});

test("a release that is already published is left alone and the run says why", () => {
  const release = { exists: true, isDraft: false, assets: [] };
  const { gh, calls } = fakeGh(release);
  assert.throws(
    () =>
      publish({
        tag: "v1.0.0-rc.17",
        notesFile: "notes.md",
        prerelease: true,
        files,
        gh,
      }),
    /already published.*new tag/,
  );
  assert.deepEqual(
    calls.map((c) => c.split(" ")[1]),
    ["view"],
  );
});

test("a stable release is published as the latest", () => {
  const release = { exists: false };
  const { gh, calls } = fakeGh(release);
  publish({
    tag: "v1.0.0",
    notesFile: "notes.md",
    prerelease: false,
    files,
    gh,
  });
  assert.ok(
    !calls.find((c) => c.includes(" create ")).includes("--prerelease"),
  );
  assert.match(calls.at(-1), /--draft=false --latest/);
});

test("a file attached at the wrong size counts as missing", () => {
  assert.deepEqual(
    missingFromDraft(files, [
      { name: "Weir-win-Setup.exe", size: 99 },
      { name: "weir-web-dist.zip", size: 50 },
    ]),
    ["Weir-win-Setup.exe"],
  );
});
