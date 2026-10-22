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
