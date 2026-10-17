// A download pinned by SHA-256: fetched from its upstream URL first, then, when upstream no longer has it, from
// Weir's own GitHub releases, which carry a copy of every third-party archive they were built with
// (release.yml). The same pin applies to both sources, and bytes that do not match it are refused wherever they
// came from. Weir's releases are found from the public release feed and their download addresses, which do not
// count against GitHub's API allowance (60 an hour without a token, shared by the whole network); the API's
// release list is asked only when those find nothing. packaging/windows/pinned-download.ps1 does the same for the
// Windows package scripts.
import { createHash } from "node:crypto";
import { createReadStream, createWriteStream } from "node:fs";
import { pipeline } from "node:stream/promises";
import { Readable } from "node:stream";

import { API_REPOSITORY_URL, REPOSITORY_URL } from "./repository.mjs";

export const weirReleasesUrl = `${API_REPOSITORY_URL}/releases?per_page=30`;
export const weirReleasesFeedUrl = `${REPOSITORY_URL}/releases.atom`;

const attempts = 4;
const attemptTimeoutMs = 5 * 60 * 1000;

class HttpError extends Error {
  constructor(status) {
    super(`HTTP ${status}`);
    this.status = status;
  }
}

async function downloadOnce(url, file, { fetchImpl, headers }) {
  const response = await fetchImpl(url, { headers, signal: AbortSignal.timeout(attemptTimeoutMs) });
  if (!response.ok) throw new HttpError(response.status);
  await pipeline(Readable.fromWeb(response.body), createWriteStream(file));
}

/** Downloads `url` into `file`, trying again after a transient failure; a 404 is final and is not tried again. */
async function download(url, file, { fetchImpl, headers = {}, retryDelayMs }) {
  let lastError;
  for (let attempt = 1; attempt <= attempts; attempt += 1) {
    try {
      await downloadOnce(url, file, { fetchImpl, headers });
      return;
    } catch (error) {
      lastError = error;
      console.error(`Download attempt ${attempt} of ${attempts} failed: ${error.message}`);
      if (error.status === 404) break;
      if (attempt < attempts) await new Promise((resolve) => setTimeout(resolve, attempt * retryDelayMs));
    }
  }
  throw new Error(`Could not download ${url}: ${lastError.message}`);
}

export async function sha256Of(file) {
  const hash = createHash("sha256");
  await pipeline(createReadStream(file), hash);
  return hash.digest("hex");
}

/** The tags of Weir's recent releases, newest first, from the public release feed. */
export async function listWeirReleaseTags({ fetchImpl = fetch } = {}) {
  const response = await fetchImpl(weirReleasesFeedUrl, { signal: AbortSignal.timeout(attemptTimeoutMs) });
  if (!response.ok) throw new Error(`Weir's release feed could not be read (HTTP ${response.status}).`);
  return [...(await response.text()).matchAll(/href="[^"]*\/releases\/tag\/([^"]+)"/g)].map((match) => decodeURIComponent(match[1]));
}

/** The download URL of the newest Weir release that has an asset named exactly `fileName`, from the API's release list. */
export async function findWeirReleaseAssetUrl(fileName, { fetchImpl = fetch, token = process.env.GITHUB_TOKEN || process.env.GH_TOKEN } = {}) {
  const headers = { Accept: "application/vnd.github+json", "User-Agent": "weir-build" };
  if (token) headers.Authorization = `Bearer ${token}`;
  const response = await fetchImpl(weirReleasesUrl, { headers, signal: AbortSignal.timeout(attemptTimeoutMs) });
  if (!response.ok) throw new Error(`Weir's releases could not be listed (HTTP ${response.status}).`);
  for (const release of await response.json()) {
    if (release.draft) continue;
    const asset = release.assets?.find((candidate) => candidate.name === fileName);
    if (asset) return asset.browser_download_url;
  }
  throw new Error(`No Weir release has an asset named ${fileName}.`);
}

/**
 * Downloads `fileName` into `file` from the newest Weir release that carries it and returns where it came from: each
 * release in the release feed is tried in turn, and the API's release list is asked only when the feed cannot be read
 * or none of its releases has the file.
 */
async function downloadFromWeirReleases(fileName, file, { fetchImpl, token, retryDelayMs }) {
  const tags = await listWeirReleaseTags({ fetchImpl }).catch((error) => {
    console.error(`${error.message}. Asking the API for the release list instead.`);
    return [];
  });
  for (const tag of tags) {
    const source = `${REPOSITORY_URL}/releases/download/${encodeURIComponent(tag)}/${encodeURIComponent(fileName)}`;
    try {
      await download(source, file, { fetchImpl, retryDelayMs });
      return source;
    } catch {
      // This release does not carry the file; the next one may.
    }
  }
  const source = await findWeirReleaseAssetUrl(fileName, { fetchImpl, token });
  await download(source, file, { fetchImpl, retryDelayMs });
  return source;
}

/**
 * Downloads `url` into `file` and checks it against `sha256`, taking the same-named asset from Weir's own releases
 * when upstream cannot be downloaded from. A hash mismatch throws and falls back to nothing.
 */
export async function downloadPinned({ url, fileName, file, sha256, fetchImpl = fetch, token, retryDelayMs = 5000 }) {
  let source = url;
  try {
    await download(url, file, { fetchImpl, retryDelayMs });
  } catch (upstreamError) {
    console.error(`${upstreamError.message}. Looking for ${fileName} on Weir's own releases.`);
    try {
      source = await downloadFromWeirReleases(fileName, file, { fetchImpl, token, retryDelayMs });
    } catch (fallbackError) {
      throw new Error(`${upstreamError.message}, and Weir's own releases could not supply it: ${fallbackError.message}`);
    }
  }
  const actual = await sha256Of(file);
  if (actual !== sha256) {
    throw new Error(`${fileName} hash mismatch. Expected ${sha256} but got ${actual} from ${source}.`);
  }
}
