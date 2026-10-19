/**
 * Weir's canonical public repository, and the licence it is released under. Static (not build-time
 * configured like VITE_SUPPORT_URL in ./support.ts): unlike the optional support link, System › About
 * always shows where the source is and what licence covers it, in every build.
 *
 * The one place the web app writes the repository's owner and name; scripts/check-repository-owner.mjs fails when
 * another file does, or when this differs from the server's, the tray's and the scripts'.
 */
const REPOSITORY_SLUG = "EJM-Digital/Weir";
export const REPOSITORY_URL = `https://github.com/${REPOSITORY_SLUG}`;
export const LICENSE_URL = `${REPOSITORY_URL}/blob/main/LICENSE`;
