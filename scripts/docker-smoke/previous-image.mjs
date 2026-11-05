// Which published image a build is upgraded from.
import { findPreviousVersion } from "../find-previous-release.mjs";
import { IMAGE_REPOSITORY } from "../repository.mjs";
import { compareSemver, parseSemver } from "../semver.mjs";

/** A published release's image. Every release, those made before the move included, is under the current name. */
export const releaseImage = (version) => `${IMAGE_REPOSITORY}:${version}`;

function newestVersion(tags) {
  let newest = null;
  for (const tag of tags) {
    const candidate = parseSemver(tag);
    if (candidate && (newest === null || compareSemver(candidate, newest) > 0)) newest = candidate;
  }
  return newest?.version ?? null;
}

/**
 * The release the build is upgraded from, and its image: the newest published release older than `releasing`, or, for a build
 * that is not a release (`releasing` empty), the newest published release. Null when there is none.
 */
export function previousRelease(tags, releasing) {
  const version = releasing ? findPreviousVersion(tags, releasing) : newestVersion(tags);
  return version ? { version, image: releaseImage(version) } : null;
}

const ABSENT = /manifest unknown|no such manifest|name unknown|not found/i;

/**
 * Whether the registry holds `image`: asks it with `docker manifest inspect`. A manifest the registry says it does not have is false;
 * any other failure (the network, a throttle, a refusal) throws with the registry's words, so it is never taken for a missing image.
 * @param {(command: string) => {status: number|null, output: string}} runForStatus Injected by the caller (and the tests).
 */
export function imageIsPublished(image, runForStatus) {
  const result = runForStatus(`docker manifest inspect ${image}`);
  if (result.status === 0) return true;
  if (ABSENT.test(result.output)) return false;
  throw new Error(`docker manifest inspect ${image} exited ${result.status}\n${result.output}`);
}

/**
 * The release a Docker build is upgraded from: the newest release older than `releasing` (or, with `releasing` empty, the newest at all)
 * whose image is in the registry. A release is published on GitHub before its image is pushed (`publish-windows` and `publish-docker`
 * do not wait for each other), and a Docker user can only upgrade from an image that exists, so a newer release without an image is
 * passed over and named in `log` with the one chosen instead. Null when no release has an image.
 */
export function previousPublishedRelease(tags, releasing, hasImage, log = console.log) {
  let remaining = tags;
  const passedOver = [];
  for (;;) {
    const candidate = previousRelease(remaining, releasing);
    if (!candidate) return null;
    if (hasImage(candidate.image)) {
      if (passedOver.length > 0) {
        log(`${passedOver.join(", ")} ${passedOver.length === 1 ? "has" : "have"} a GitHub release but no image yet; upgrading from ${candidate.version}.`);
      }
      return candidate;
    }
    passedOver.push(candidate.version);
    remaining = remaining.filter((tag) => parseSemver(tag)?.version !== candidate.version);
  }
}
