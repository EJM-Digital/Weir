#!/usr/bin/env node
// Names the newest Deluno release as an image reference with a digest, for the weekly drift check (.github/workflows/record-deluno.yml).
//
// Neither of the registry's orderings is "newest". Deluno's `latest` tag only moves with a stable release, so while Deluno publishes release
// candidates it stays on an old build (in October 2026 an August one that did not speak the hand-off Weir expects, which hung the drift check
// for 45 minutes). The highest version is no better: Deluno's old 1.1.x stable line outranks its 1.0.0-rc.30.x candidates. The newest release
// is what Deluno's own repository published last, and the image carries that release's version as a tag; the digest is what the registry says
// that tag points at.
//
// The releases are those of the GitHub repository named like the image (ghcr.io/<owner>/<name> is github.com/<owner>/<name>).
//
// Usage: node scripts/newest-deluno-image.mjs <image repository>   (prints "<tag> <repository>@sha256:...")
import { pathToFileURL } from "node:url";

const MANIFEST_TYPES =
  "application/vnd.oci.image.index.v1+json,application/vnd.oci.image.manifest.v1+json,application/vnd.docker.distribution.manifest.v2+json,application/vnd.docker.distribution.manifest.list.v2+json";

/** The image tag of the newest published release: the one published last, drafts left out, without the "v" Deluno's release tags carry. */
export function newestReleaseTag(releases) {
  const published = releases.filter((release) => !release.draft && release.published_at).sort((a, b) => Date.parse(b.published_at) - Date.parse(a.published_at));
  if (published.length === 0) throw new Error("Deluno has published no release, so there is no newest one to name.");
  return String(published[0].tag_name).replace(/^v/, "");
}

/** The newest release's image tag and its reference with a digest, as the registry reports them. */
export async function newestDelunoImage(repository, fetchImpl = fetch) {
  const [host, ...path] = repository.split("/");
  if (host !== "ghcr.io" || path.length < 2) throw new Error(`${repository} is not a ghcr.io repository.`);
  const name = path.join("/");

  const listed = await fetchImpl(`https://api.github.com/repos/${name}/releases?per_page=30`, { headers: { Accept: "application/vnd.github+json" } });
  if (!listed.ok) throw new Error(`GitHub would not list Deluno's releases (answered ${listed.status}).`);
  const tag = newestReleaseTag(await listed.json());

  const token = (await (await fetchImpl(`https://ghcr.io/token?scope=repository:${name}:pull`)).json()).token;
  const manifest = await fetchImpl(`https://ghcr.io/v2/${name}/manifests/${tag}`, { method: "HEAD", headers: { Authorization: `Bearer ${token}`, Accept: MANIFEST_TYPES } });
  const digest = manifest.headers.get("docker-content-digest");
  if (!manifest.ok || !/^sha256:[0-9a-f]{64}$/.test(digest ?? "")) throw new Error(`The registry gave no digest for ${repository}:${tag} (answered ${manifest.status}).`);
  return { tag, reference: `${repository}@${digest}` };
}

/** Prints the newest release's tag and image reference for the repository named on the command line; `newest` and `log` are what a test replaces. */
export async function main(argv, { newest = newestDelunoImage, log = console.log } = {}) {
  const { tag, reference } = await newest(argv[0]);
  log(`${tag} ${reference}`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main(process.argv.slice(2));
