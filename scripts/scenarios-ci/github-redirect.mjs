// Sends the tray's requests for github.com to this PC, so the tray updates from a feed this run controls while it runs its own, unchanged
// update code. Two things change on the PC, both undone at the end: the hosts file names github.com and api.github.com as this PC, and a
// certificate made here for those two names is trusted, so the tray (which trusts the PC's own store) accepts the answer. Meant for a
// throwaway GitHub-hosted runner; the entry point refuses any other machine before this is reached.
export const NAMES = ["github.com", "api.github.com"];
const MARK = "# weir-scenario";

/** The hosts file's text with each name pointing at this PC, and nothing left of an earlier run's lines. */
export function hostsRedirecting(text, names = NAMES) {
  const kept = hostsRestored(text);
  const lines = names.map((name) => `127.0.0.1 ${name} ${MARK}`);
  return `${kept.replace(/\s*$/, "")}\r\n${lines.join("\r\n")}\r\n`;
}

/** The hosts file's text without the lines this run added. */
export function hostsRestored(text) {
  return text
    .split(/\r?\n/)
    .filter((line) => !line.trimEnd().endsWith(MARK))
    .join("\r\n");
}
