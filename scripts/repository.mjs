// Where Weir's source, releases and container image live: the one place the Node scripts write the repository's
// owner and name. The server, the tray, the web app and the PowerShell scripts each keep their own copy, and
// check-repository-owner.mjs fails when any of them differs or the name is written anywhere else.
export const REPOSITORY = "EJM-Digital/Weir";
export const REPOSITORY_URL = `https://github.com/${REPOSITORY}`;
export const API_REPOSITORY_URL = `https://api.github.com/repos/${REPOSITORY}`;

/** The container image's name, which the registry needs in lower case. */
export const IMAGE_REPOSITORY = `ghcr.io/${REPOSITORY.toLowerCase()}`;

/**
 * Releases up to and including this one were published before the move to EJM-Digital (20 Oct 2026), under the former owner's
 * image name, which ghcr does not forward. A test that starts from an older release pulls it from there.
 */
export const LAST_RELEASE_BEFORE_MOVE = "1.0.0-rc.18";
export const FORMER_IMAGE_REPOSITORY = "ghcr.io/jampat000/weir";

/** Where Weir lived before, which GitHub redirects to where it lives now. A release made before the move asks for these. */
export const FORMER_REPOSITORIES = ["jampat000/Weir"];
