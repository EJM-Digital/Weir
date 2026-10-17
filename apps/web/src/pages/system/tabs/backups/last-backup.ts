import type { ConfigurationBackupItem } from "../../../../lib/settings/types";
import { parseAppTime } from "../../../../lib/ui/mm-format-date";

/** The backup made last, by the schedule or by hand. A backup whose time cannot be read is left out. */
export function newestBackup(
  items: readonly ConfigurationBackupItem[],
): ConfigurationBackupItem | null {
  let newest: ConfigurationBackupItem | null = null;
  let newestAt = -Infinity;
  for (const item of items) {
    const at = parseAppTime(item.created_at);
    if (at !== null && at > newestAt) {
      newest = item;
      newestAt = at;
    }
  }
  return newest;
}

/** Who made a backup, in words; empty for one made before that was kept. */
export function madeByWords(
  madeBy: ConfigurationBackupItem["made_by"],
): string {
  if (madeBy === "manual") return "by hand";
  if (madeBy === "schedule") return "scheduled";
  return "";
}
