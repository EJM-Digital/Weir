// A compose file laid over the shipped compose.yaml, so a scenario can set the variables, folders and platform an operator would add while
// the file's own cap_drop, capabilities and security options stay exactly as they ship.

/**
 * The text of an override for the `weir` service.
 * @param {object} [layers]
 * @param {Record<string, string>} [layers.environment] Variables added to the service's.
 * @param {string[]} [layers.binds] Bind mounts as `host:container`.
 * @param {string} [layers.platform] For example `linux/arm64`.
 * @param {string} [layers.pullPolicy] Replaces the shipped `pull_policy`, for commands that have no --pull option of their own.
 */
export function composeOverride({ environment = {}, binds = [], platform, pullPolicy } = {}) {
  // JSON strings are YAML double-quoted scalars, so no value needs its own escaping.
  const quote = (value) => JSON.stringify(String(value));
  const lines = ["services:", "  weir:"];
  if (platform) lines.push(`    platform: ${quote(platform)}`);
  if (pullPolicy) lines.push(`    pull_policy: ${quote(pullPolicy)}`);
  const names = Object.keys(environment);
  if (names.length > 0) {
    lines.push("    environment:");
    for (const name of names) lines.push(`      ${quote(name)}: ${quote(environment[name])}`);
  }
  if (binds.length > 0) {
    lines.push("    volumes:");
    for (const bind of binds) lines.push(`      - ${quote(bind)}`);
  }
  return `${lines.join("\n")}\n`;
}
