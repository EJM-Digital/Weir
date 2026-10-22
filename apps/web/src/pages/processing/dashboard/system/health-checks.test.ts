import { describe, expect, it } from "vitest";

import type { ConnectionEntry } from "../../../../lib/connections/connection-model";
import type { ProcessingLibrary } from "../../../../lib/processing/libraries-api";
import type { SystemDrive } from "../../../../lib/system/system-stats-types";
import type { ToolRow } from "../health-model";
import type { WorkflowHealth } from "../use-health";
import {
  backupChecks,
  connectionChecks,
  storageChecks,
  toolChecks,
  weirChecks,
  workflowChecks,
} from "./health-checks";

const NOW = Date.parse("2026-10-02T10:00:00Z");
const HOUR = 3_600_000;

function workflowHealth(
  overrides: Partial<WorkflowHealth> & { id: number; name: string },
): WorkflowHealth {
  const { id, name, ...rest } = overrides;
  return {
    workflow: { id, name } as ProcessingLibrary,
    verdict: { words: "In sync", meaning: "done", readiness: "ready" },
    why: null,
    chain: undefined,
    checkedAt: NOW,
    recheck: async () => undefined,
    ...rest,
  };
}

function connection(
  overrides: Partial<ConnectionEntry> & { key: string },
): ConnectionEntry {
  return {
    kind: "media_manager",
    id: 1,
    name: "Radarr",
    baseName: "Radarr",
    nickname: "",
    kindLabel: "Radarr",
    address: "http://radarr",
    enabled: true,
    state: "ok",
    checkedAt: NOW,
    answerMs: 84,
    detail: "",
    testable: true,
    ...overrides,
  };
}

describe("workflowChecks", () => {
  it("passes a workflow whose chain is in sync", () => {
    const [check] = workflowChecks([workflowHealth({ id: 2, name: "Movies" })]);
    expect(check).toMatchObject({
      meaning: "done",
      title: "Movies",
      fix: null,
      workflowId: 2,
      again: { area: "workflows", key: "2" },
    });
  });

  it("asks for a fix on a chain that needs one, with the reason and a link to the workflow", () => {
    const [check] = workflowChecks([
      workflowHealth({
        id: 2,
        name: "Movies",
        verdict: {
          words: "Needs a fix",
          meaning: "attention",
          readiness: "needs_attention",
        },
        why: "The output folder is missing.",
      }),
    ]);
    expect(check).toMatchObject({
      meaning: "attention",
      title: "Movies needs a fix",
      why: "The output folder is missing.",
    });
    expect(check.fix?.to).toBe("/setup/workflows?edit=2");
  });

  it("leaves a chain that is only being checked as neither passing nor failing", () => {
    const [check] = workflowChecks([
      workflowHealth({
        id: 2,
        name: "Movies",
        verdict: { words: "Checking…", meaning: "doing", readiness: null },
      }),
    ]);
    expect(check).toMatchObject({ meaning: "doing", why: "Checking…" });
  });
});

describe("a check's few words", () => {
  it("says a workflow problem in the compact Dashboard style, keeping the sentence", () => {
    const sentence =
      "Weir could not reach Radarr (4K) at http://localhost:7879. Check the address is right, and that the app is running and reachable from this machine.";
    const [check] = workflowChecks([
      workflowHealth({
        id: 2,
        name: "4K Movies",
        verdict: {
          words: "Needs a fix",
          meaning: "attention",
          readiness: "needs_attention",
        },
        why: sentence,
      }),
    ]);

    expect(check.words).toBe("Radarr (4K) not answering · check address");
    expect(check.why).toBe(sentence);
  });

  it("says a drive that fills soon, or is low, in a few words", () => {
    const drive = (changes: Partial<SystemDrive>) =>
      storageChecks([
        {
          name: "D:",
          path: "D:\\",
          total_bytes: 1000 * 1024 ** 3,
          free_bytes: 500 * 1024 ** 3,
          weir_bytes: 0,
          keep_free_bytes: 20 * 1024 ** 3,
          full_in_days: null,
          read_bytes_per_sec: null,
          write_bytes_per_sec: null,
          busy_percent: null,
          workflows: [],
          ...changes,
        },
      ])[0];

    expect(drive({ full_in_days: 2 }).words).toBe(
      "500 GB free · full in ~2 days",
    );
    expect(drive({ free_bytes: 10 * 1024 ** 3 }).words).toBe(
      "10.00 GB free · keeps 20.00 GB",
    );
    expect(drive({}).words).toBe("500 GB free");
  });

  it("marks a drive live rather than giving it a time, because it follows the stream second by second", () => {
    const [check] = storageChecks([
      {
        name: "D:",
        path: "D:\\",
        total_bytes: 1000,
        free_bytes: 500,
        weir_bytes: 0,
        keep_free_bytes: 0,
        full_in_days: null,
        read_bytes_per_sec: null,
        write_bytes_per_sec: null,
        busy_percent: null,
        workflows: [],
      },
    ]);
    expect(check).toMatchObject({ live: true, checkedAt: null });
  });
});

describe("connectionChecks", () => {
  it("fails a connection that is not answering, with what its test said", () => {
    const [check] = connectionChecks([
      connection({ key: "a", state: "down", detail: "Connection refused." }),
    ]);
    expect(check).toMatchObject({
      meaning: "broken",
      title: "Radarr isn't answering",
      why: "Connection refused.",
    });
    expect(check.fix?.to).toBe("/setup/connections");
  });

  it("warns on a connection that is slow, with how long it took", () => {
    const [check] = connectionChecks([
      connection({ key: "a", state: "slow", answerMs: 2500 }),
    ]);
    expect(check).toMatchObject({
      meaning: "attention",
      why: "Its last answer came in 2,500 ms.",
    });
  });

  it("says a connection is answering, without a time, when no time was taken", () => {
    const [check] = connectionChecks([
      connection({ key: "a", answerMs: null }),
    ]);
    expect(check).toMatchObject({ why: "Answering.", words: "Answering" });
  });

  it("passes an answering connection and leaves an untested one open", () => {
    const [ok, untested] = connectionChecks([
      connection({ key: "a" }),
      connection({ key: "b", state: "untested" }),
    ]);
    expect(ok.meaning).toBe("done");
    expect(untested.meaning).toBe("idle");
  });

  it("leaves out a connection that is switched off", () => {
    expect(
      connectionChecks([
        connection({ key: "a", enabled: false, state: "off" }),
      ]),
    ).toEqual([]);
  });

  it("looks again at the one connection", () => {
    const [check] = connectionChecks([connection({ key: "media_manager:1" })]);
    expect(check.again).toEqual({
      area: "connections",
      key: "media_manager:1",
    });
  });
});

describe("toolChecks", () => {
  const ffmpeg = (meaning: ToolRow["meaning"]): ToolRow => ({
    key: "ffmpeg",
    name: "FFmpeg",
    version: "7.1",
    banner: "ffmpeg version 7.1",
    meaning,
  });

  it("fails when FFmpeg is missing", () => {
    const [check] = toolChecks([ffmpeg("broken")], NOW);
    expect(check).toMatchObject({
      meaning: "broken",
      title: "FFmpeg is missing",
    });
  });

  it("only notes that the optional mkvmerge is missing", () => {
    const [check] = toolChecks(
      [{ ...ffmpeg("idle"), key: "mkvmerge", name: "mkvmerge" }],
      NOW,
    );
    expect(check).toMatchObject({ meaning: "idle", fact: true });
  });

  it("passes a tool that is installed and says its version line", () => {
    const [check] = toolChecks([ffmpeg("done")], NOW);
    expect(check).toMatchObject({ meaning: "done", why: "ffmpeg version 7.1" });
  });

  it("has no checks while the tools have not answered", () => {
    expect(toolChecks(null, null)).toEqual([]);
  });
});

describe("storageChecks", () => {
  function drive(overrides: Partial<SystemDrive>): SystemDrive {
    return {
      name: "D:",
      path: "D:\\",
      total_bytes: 2_000_000_000_000,
      free_bytes: 1_500_000_000_000,
      weir_bytes: 0,
      keep_free_bytes: 20_000_000_000,
      full_in_days: null,
      read_bytes_per_sec: null,
      write_bytes_per_sec: null,
      busy_percent: null,
      workflows: [{ id: 3, name: "Movies", roles: ["output"] }],
      ...overrides,
    };
  }

  it("fails a drive with less free than Weir keeps free, and links to a workflow that writes there", () => {
    const [check] = storageChecks([drive({ free_bytes: 5_000_000_000 })]);
    expect(check).toMatchObject({
      meaning: "attention",
      title: "D: is low on space",
    });
    expect(check.fix?.to).toBe("/setup/workflows?edit=3");
  });

  it("warns on a drive that fills within a week", () => {
    const [check] = storageChecks([drive({ full_in_days: 3 })]);
    expect(check).toMatchObject({
      meaning: "attention",
      title: "D: fills up soon",
    });
    expect(check.why).toContain("about 3 days");
  });

  it("passes a drive with room, and says how much", () => {
    const [check] = storageChecks([drive({})]);
    expect(check.meaning).toBe("done");
    expect(check.why).toMatch(/free\.$/);
  });

  it("gives a drive no time of its own, because it follows the stream", () => {
    const [check] = storageChecks([drive({})]);
    expect(check.checkedAt).toBeNull();
  });

  it("has no checks while the drives have not been read", () => {
    expect(storageChecks(null)).toEqual([]);
  });
});

describe("backupChecks", () => {
  const facts = {
    enabled: true,
    intervalHours: 24,
    lastBackupAt: NOW - HOUR,
    checkedAt: NOW,
  };

  it("passes while the newest backup is within the schedule", () => {
    expect(backupChecks(facts, NOW)[0].meaning).toBe("done");
  });

  it("warns when automatic backups are off", () => {
    const [check] = backupChecks({ ...facts, enabled: false }, NOW);
    expect(check).toMatchObject({ meaning: "attention" });
    expect(check.fix?.to).toBe("/system?tab=backups");
  });

  it("warns when no backup has been made", () => {
    const [check] = backupChecks({ ...facts, lastBackupAt: null }, NOW);
    expect(check.why).toBe("No backup has been made yet.");
  });

  it("warns when the newest backup is older than two intervals", () => {
    const [check] = backupChecks(
      { ...facts, lastBackupAt: NOW - 49 * HOUR },
      NOW,
    );
    expect(check.meaning).toBe("attention");
  });

  it("has no check before the settings have loaded", () => {
    expect(backupChecks(null, NOW)).toEqual([]);
  });
});

describe("weirChecks", () => {
  it("fails when a worker has stopped and says what it reported", () => {
    const [check] = weirChecks({
      stoppedWorkers: ["The cleanup worker stopped."],
      updateVersion: null,
      checkedAt: NOW,
    });
    expect(check).toMatchObject({
      meaning: "broken",
      why: "The cleanup worker stopped.",
    });
  });

  it("passes when every worker runs, and notes a newer version without counting it", () => {
    const checks = weirChecks({
      stoppedWorkers: [],
      updateVersion: "3.3.0",
      checkedAt: NOW,
    });
    expect(checks.map((check) => [check.meaning, check.fact])).toEqual([
      ["done", undefined],
      ["todo", true],
    ]);
    expect(checks[1].title).toBe("Weir 3.3.0 is out");
  });

  it("has no checks before readiness has answered", () => {
    expect(weirChecks(null)).toEqual([]);
  });
});
