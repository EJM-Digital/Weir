// The weekly drift check records against Deluno's newest release. Its `latest` tag and its highest version are both older builds (October 2026:
// `latest` was an August build, and the 1.1.x stable line outranks the 1.0.0-rc.30.x candidates), so the newest is the release published last.
import assert from "node:assert/strict";
import { test } from "node:test";

import { newestDelunoImage, newestReleaseTag } from "./newest-deluno-image.mjs";

// What Deluno's releases looked like on 23 October 2026, newest first as GitHub lists them, with a draft and an older stable line among them.
const RELEASES = [
  { tag_name: "v1.0.0-rc.30.36", draft: true, published_at: null },
  { tag_name: "v1.0.0-rc.30.35", draft: false, published_at: "2026-10-23T11:40:13Z" },
  { tag_name: "v1.0.0-rc.30.33", draft: false, published_at: "2026-10-17T09:16:35Z" },
  { tag_name: "v1.1.2", draft: false, published_at: "2026-08-18T10:00:00Z" },
];

test("the newest release is the one published last: not a draft, not the highest version, not 'latest'", () => {
  assert.equal(newestReleaseTag(RELEASES), "1.0.0-rc.30.35");
  assert.equal(newestReleaseTag([...RELEASES].reverse()), "1.0.0-rc.30.35", "whatever order GitHub lists them in");
});

test("no published release is an error that says so, instead of falling back to a tag", () => {
  assert.throws(() => newestReleaseTag([{ tag_name: "v1.0.0", draft: true }]), /no release/);
  assert.throws(() => newestReleaseTag([]), /no release/);
});

function services({ releases = RELEASES, digests = {}, releasesStatus = 200 } = {}) {
  const calls = [];
  const fetchImpl = async (url, options = {}) => {
    calls.push(`${options.method ?? "GET"} ${url}`);
    if (url.startsWith("https://api.github.com/repos/example/deluno/releases")) return releasesStatus === 200 ? Response.json(releases) : new Response("", { status: releasesStatus });
    if (url.startsWith("https://ghcr.io/token")) return Response.json({ token: "t" });
    const tag = url.split("/manifests/")[1];
    return digests[tag] ? new Response(null, { status: 200, headers: { "docker-content-digest": digests[tag] } }) : new Response("", { status: 404 });
  };
  return { fetchImpl, calls };
}

test("the newest Deluno is named by the digest the registry gives its release's tag", async () => {
  const digest = `sha256:${"2".repeat(64)}`;
  const { fetchImpl, calls } = services({ digests: { "1.0.0-rc.30.35": digest, latest: `sha256:${"3".repeat(64)}` } });
  assert.deepEqual(await newestDelunoImage("ghcr.io/example/deluno", fetchImpl), { tag: "1.0.0-rc.30.35", reference: `ghcr.io/example/deluno@${digest}` });
  assert.ok(calls.every((call) => !call.endsWith("/latest")), "the latest tag is never asked for");
});

test("a release whose image the registry does not have fails with the tag in the message", async () => {
  await assert.rejects(() => newestDelunoImage("ghcr.io/example/deluno", services().fetchImpl), /ghcr\.io\/example\/deluno:1\.0\.0-rc\.30\.35.*404/);
});

test("releases GitHub will not list fail with its answer", async () => {
  await assert.rejects(() => newestDelunoImage("ghcr.io/example/deluno", services({ releasesStatus: 403 }).fetchImpl), /would not list.*403/);
});

test("only a ghcr.io repository is accepted", async () => {
  await assert.rejects(() => newestDelunoImage("docker.io/library/deluno", async () => Response.json({})), /ghcr\.io/);
});
