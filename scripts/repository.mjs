// Where Weir's source, releases and container image live: the one place the Node scripts write the repository's
// owner and name. The server, the tray, the web app and the PowerShell scripts each keep their own copy, and
// check-repository-owner.mjs fails when any of them differs or the name is written anywhere else.
export const REPOSITORY = "jampat000/Weir";
export const REPOSITORY_URL = `https://github.com/${REPOSITORY}`;
export const API_REPOSITORY_URL = `https://api.github.com/repos/${REPOSITORY}`;

/** The container image's name, which the registry needs in lower case. */
export const IMAGE_REPOSITORY = `ghcr.io/${REPOSITORY.toLowerCase()}`;
