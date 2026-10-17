// node --test scripts/pinned-download.test.mjs
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";

import { REPOSITORY_URL } from "./repository.mjs";
import { downloadPinned, findWeirReleaseAssetUrl, listWeirReleaseTags, weirReleasesFeedUrl, weirReleasesUrl } from "./pinned-download.mjs";

const fileName = "ffmpeg-n9.0.2-23-g27b46f0fbc-linux64-lgpl-9.0.tar.xz";
const upstreamUrl = `https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-1/${fileName}`;
const weirAssetUrl = `${REPOSITORY_URL}/releases/download/v1.0.0-rc.9/${fileName}`;
const newerWeirAssetUrl = `${REPOSITORY_URL}/releases/download/v1.0.0-rc.10/${fileName}`;
const content = "the archive";

/** The public release feed, newest release first, as GitHub writes it: the tag is in each entry's link. */
const releaseFeed = (...tags) =>
  `<feed xmlns="http://www.w3.org/2005/Atom">${tags
    .map((tag) => `<entry><link rel="alternate" type="text/html" href="${REPOSITORY_URL}/releases/tag/${tag}"/><title>${tag}</title><content type="html">&lt;a href=&quot;${REPOSITORY_URL}/releases/tag/v0.0.1&quot;&gt;old&lt;/a&gt;</content></entry>`)
    .join("")}</feed>`;
const sha256 = createHash("sha256").update(content).digest("hex");

const asset = (name, url = `https://example.test/${name}`) => ({ name, browser_download_url: url });

/** A fetch that answers each URL from `responses` (a body, a JSON value, or `queue(...)` of statuses in turn) and records every request. */
const queue = (...statuses) => ({ queue: statuses });

function fakeFetch(responses) {
  const requests = [];
  const fetchImpl = async (url, options = {}) => {
    requests.push({ url, headers: options.headers ?? {} });
    const answer = responses[url];
    if (answer === undefined) return new Response("not found", { status: 404 });
    if (answer.queue) {
      const next = answer.queue.shift();
      return new Response(next.body ?? "error", { status: next.status });
    }
    return new Response(typeof answer === "string" ? answer : JSON.stringify(answer), { status: 200 });
  };
  return { fetchImpl, requests };
}

async function withDestination(run) {
  const dir = mkdtempSync(path.join(tmpdir(), "weir-pinned-download-"));
  try {
    return await run(path.join(dir, fileName));
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

const download = (file, fetchImpl, overrides = {}) =>
  downloadPinned({ url: upstreamUrl, fileName, file, sha256, fetchImpl, token: "", retryDelayMs: 0, ...overrides });

test("an archive upstream still has is taken from upstream, and Weir's releases are not asked", async () => {
  const { fetchImpl, requests } = fakeFetch({ [upstreamUrl]: content });
  await withDestination(async (file) => {
    await download(file, fetchImpl);
    assert.equal(readFileSync(file, "utf8"), content);
  });
  assert.deepEqual(requests.map((request) => request.url), [upstreamUrl]);
});

test("an archive upstream no longer has is taken from the newest release in the release feed that carries it, without the API", async () => {
  const { fetchImpl, requests } = fakeFetch({
    [weirReleasesFeedUrl]: releaseFeed("v1.0.0-rc.10", "v1.0.0-rc.9", "v1.0.0-rc.8"),
    [weirAssetUrl]: content,
  });
  await withDestination(async (file) => {
    await download(file, fetchImpl);
    assert.equal(readFileSync(file, "utf8"), content);
  });
  assert.deepEqual(requests.map((request) => request.url), [upstreamUrl, weirReleasesFeedUrl, newerWeirAssetUrl, weirAssetUrl]);
});

test("a release feed that cannot be read, or whose releases lack the archive, leaves the API's release list to answer", async () => {
  for (const feed of [undefined, releaseFeed("v1.0.0-rc.10")]) {
    const { fetchImpl, requests } = fakeFetch({
      ...(feed && { [weirReleasesFeedUrl]: feed }),
      [weirReleasesUrl]: [{ draft: false, assets: [asset(fileName, weirAssetUrl)] }],
      [weirAssetUrl]: content,
    });
    await withDestination((file) => download(file, fetchImpl));
    assert.equal(requests.some((request) => request.url === weirReleasesUrl), true);
  }
});

test("the tags in the release feed are read newest first, from links and not from the release notes", async () => {
  const { fetchImpl } = fakeFetch({ [weirReleasesFeedUrl]: releaseFeed("v1.0.0-rc.10", "untagged-1b2c3d") });
  assert.deepEqual(await listWeirReleaseTags({ fetchImpl }), ["v1.0.0-rc.10", "untagged-1b2c3d"]);
});

test("an archive upstream no longer has is taken from the newest Weir release that carries it when the feed is silent", async () => {
  const { fetchImpl, requests } = fakeFetch({
    [weirReleasesUrl]: [
      { draft: false, assets: [asset("Weir-win-Setup.exe")] },
      { draft: true, assets: [asset(fileName, "https://example.test/draft")] },
      { draft: false, assets: [asset(fileName, weirAssetUrl)] },
      { draft: false, assets: [asset(fileName, "https://example.test/older")] },
    ],
    [weirAssetUrl]: content,
  });
  await withDestination(async (file) => {
    await download(file, fetchImpl);
    assert.equal(readFileSync(file, "utf8"), content);
  });
  assert.deepEqual(requests.map((request) => request.url), [upstreamUrl, weirReleasesFeedUrl, weirReleasesUrl, weirAssetUrl]);
});

test("a missing upstream archive is not tried again, a failing one is", async () => {
  const missing = fakeFetch({ [weirReleasesUrl]: [], [weirAssetUrl]: content });
  await withDestination((file) => download(file, missing.fetchImpl)).catch(() => {});
  assert.equal(missing.requests.filter((request) => request.url === upstreamUrl).length, 1);

  const flaky = fakeFetch({ [upstreamUrl]: queue({ status: 503 }, { status: 503 }, { status: 200, body: content }) });
  await withDestination((file) => download(file, flaky.fetchImpl));
  assert.equal(flaky.requests.filter((request) => request.url === upstreamUrl).length, 3);
  assert.equal(flaky.requests.some((request) => request.url === weirReleasesUrl), false);
});

test("when no Weir release carries the archive either, the failure names both sources", async () => {
  const { fetchImpl } = fakeFetch({ [weirReleasesUrl]: [{ draft: false, assets: [asset("Weir-win-Setup.exe")] }] });
  await withDestination((file) => assert.rejects(download(file, fetchImpl), (error) => {
    assert.match(error.message, new RegExp(`Could not download ${upstreamUrl.replaceAll(".", "\\.")}: HTTP 404`));
    assert.match(error.message, new RegExp(`No Weir release has an asset named ${fileName.replaceAll(".", "\\.")}`));
    return true;
  }));
});

test("bytes from Weir's release that do not match the pin are refused", async () => {
  const { fetchImpl } = fakeFetch({
    [weirReleasesUrl]: [{ draft: false, assets: [asset(fileName, weirAssetUrl)] }],
    [weirAssetUrl]: "something else",
  });
  await withDestination((file) => assert.rejects(download(file, fetchImpl), new RegExp(`${fileName.replaceAll(".", "\\.")} hash mismatch.*from ${weirAssetUrl.replaceAll(".", "\\.")}`)));
});

test("bytes from upstream that do not match the pin are refused without falling back", async () => {
  const { fetchImpl, requests } = fakeFetch({ [upstreamUrl]: "rewritten upstream", [weirReleasesUrl]: [{ draft: false, assets: [asset(fileName, weirAssetUrl)] }], [weirAssetUrl]: content });
  await withDestination((file) => assert.rejects(download(file, fetchImpl), /hash mismatch/));
  assert.deepEqual(requests.map((request) => request.url), [upstreamUrl]);
});

test("the token authorises the release listing and is not sent to a download", async () => {
  const { fetchImpl, requests } = fakeFetch({
    [weirReleasesUrl]: [{ draft: false, assets: [asset(fileName, weirAssetUrl)] }],
    [weirAssetUrl]: content,
  });
  await withDestination((file) => download(file, fetchImpl, { token: "secret" }));
  const listing = requests.find((request) => request.url === weirReleasesUrl);
  assert.equal(listing.headers.Authorization, "Bearer secret");
  assert.equal(requests.some((request) => request.url !== weirReleasesUrl && "Authorization" in request.headers), false);
});

test("a release listing that fails is reported with its status", async () => {
  const { fetchImpl } = fakeFetch({ [weirReleasesUrl]: queue({ status: 403 }) });
  await assert.rejects(findWeirReleaseAssetUrl(fileName, { fetchImpl, token: "" }), /could not be listed \(HTTP 403\)/);
});
