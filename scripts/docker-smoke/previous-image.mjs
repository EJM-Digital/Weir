// Which published image a build is upgraded from.
import { findPreviousVersion } from "../find-previous-release.mjs";
import { FORMER_IMAGE_REPOSITORY, IMAGE_REPOSITORY, LAST_RELEASE_BEFORE_MOVE } from "../repository.mjs";
import { compareSemver, parseSemver } from "../semver.mjs";

/** A published release's image: under the former owner's name for a release made before the move, under the current one after it. */
export function releaseImage(version) {
  const moved = compareSemver(parseSemver(version), parseSemver(LAST_RELEASE_BEFORE_MOVE)) > 0;
  return `${moved ? IMAGE_REPOSITORY : FORMER_IMAGE_REPOSITORY}:${version}`;
}

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
