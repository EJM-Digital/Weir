import { describe, expect, it } from "vitest";

import type { ConfigurationBackupItem } from "../../../../lib/settings/types";
import { madeByWords, newestBackup } from "./last-backup";

function backup(
  id: number,
  created_at: string,
  made_by: ConfigurationBackupItem["made_by"],
): ConfigurationBackupItem {
  return {
    id,
    created_at,
    file_name: `b${id}.json`,
    size_bytes: 1,
    made_by,
    secrets_sealed: false,
  };
}

describe("newestBackup", () => {
  it("is the backup made last, whoever made it", () => {
    const scheduled = backup(1, "2026-10-02T02:00:00Z", "schedule");
    const byHand = backup(2, "2026-10-02T15:30:00Z", "manual");

    expect(newestBackup([byHand, scheduled])).toBe(byHand);
    expect(newestBackup([scheduled, byHand])).toBe(byHand);
  });

  it("is none when there are no backups, or none with a time that reads", () => {
    expect(newestBackup([])).toBeNull();
    expect(newestBackup([backup(1, "never", "manual")])).toBeNull();
  });
});

describe("madeByWords", () => {
  it("says whether the schedule or a person made the backup", () => {
    expect(madeByWords("schedule")).toBe("scheduled");
    expect(madeByWords("manual")).toBe("by hand");
  });

  it("says nothing for a backup made before it was kept", () => {
    expect(madeByWords(null)).toBe("");
  });
});
