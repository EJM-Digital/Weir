import { afterEach, describe, expect, it, vi } from "vitest";

import {
  configurationBackupsPath,
  createNotificationChannel,
  deleteNotificationChannel,
  fetchAppSettings,
  fetchConfigurationBackupList,
  fetchConfigurationBundle,
  fetchNotificationChannels,
  fetchOperationalHistoryPreview,
  fetchSecurityOverview,
  fetchServerMetrics,
  fetchStoredConfigurationBackupBlob,
  fetchUpdateSettings,
  fetchUpdateState,
  postApplyUpdate,
  postCheckUpdate,
  postConfigurationBackupNow,
  postDownloadUpdate,
  putAppSettings,
  putBackupPassword,
  putConfigurationBundle,
  putNetworkAccess,
  putUpdateSettings,
  resetOperationalHistory,
  testNotificationChannel,
  updateNotificationChannel,
} from "./settings-api";
import { unexpectedProblem } from "../../test/stub-weir-api";
import type { NotificationChannelIn } from "./types";

const CHANNEL = { name: "Phone" } as unknown as NotificationChannelIn;

/** The token request is answered; every other request fails as the server does when nobody planned for it: a 500 that gives no reason. */
function refuseEverythingButTheToken() {
  vi.stubGlobal(
    "fetch",
    vi.fn((url: string) => {
      const isToken = url.endsWith("/api/v1/auth/csrf");
      return Promise.resolve(
        isToken
          ? new Response(JSON.stringify({ csrf_token: "t" }), { status: 200 })
          : unexpectedProblem(),
      );
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("what the settings screens say when the server refuses without a reason", () => {
  it.each([
    ["loading settings", () => fetchAppSettings(), "Could not load settings"],
    [
      "saving settings",
      () => putAppSettings({} as never),
      "Could not save settings",
    ],
    [
      "loading the security overview",
      () => fetchSecurityOverview(),
      "Could not load security overview",
    ],
    [
      "changing network access",
      () => putNetworkAccess({ scope: "network" } as never),
      "Could not change network access",
    ],
    [
      "loading runtime health",
      () => fetchServerMetrics(),
      "Could not load runtime health",
    ],
    [
      "loading update settings",
      () => fetchUpdateSettings(),
      "Could not load update settings",
    ],
    [
      "saving update settings",
      () => putUpdateSettings({} as never),
      "Could not save update settings",
    ],
    [
      "loading the update state",
      () => fetchUpdateState(),
      "Could not load update state",
    ],
    [
      "asking for an update check",
      () => postCheckUpdate(),
      "Could not ask for an update check",
    ],
    [
      "asking for the download",
      () => postDownloadUpdate(),
      "Could not ask for the download",
    ],
    [
      "applying the update",
      () => postApplyUpdate(),
      "Could not signal update apply",
    ],
    [
      "previewing a history clear",
      () => fetchOperationalHistoryPreview(),
      "Could not check what clearing history would remove",
    ],
    [
      "clearing history",
      () => resetOperationalHistory("reset"),
      "Could not reset activity history",
    ],
    [
      "exporting the configuration",
      () => fetchConfigurationBundle(),
      "Could not export configuration",
    ],
    [
      "restoring the configuration",
      () => putConfigurationBundle({ format_version: 1 }),
      "Could not restore configuration",
    ],
    [
      "making a backup",
      () => postConfigurationBackupNow(),
      "Could not create a backup",
    ],
    [
      "setting the backup password",
      () => putBackupPassword("a", "a"),
      "Could not set the backup password",
    ],
    [
      "listing the automatic snapshots",
      () => fetchConfigurationBackupList(),
      "Could not load automatic snapshots",
    ],
    [
      "downloading an automatic snapshot",
      () => fetchStoredConfigurationBackupBlob(3),
      "Could not download automatic snapshot",
    ],
    [
      "listing the notification channels",
      () => fetchNotificationChannels(),
      "Could not load notification channels",
    ],
    [
      "creating a notification channel",
      () => createNotificationChannel(CHANNEL),
      "Could not create notification channel",
    ],
    [
      "updating a notification channel",
      () => updateNotificationChannel(4, CHANNEL),
      "Could not update notification channel",
    ],
    [
      "deleting a notification channel",
      () => deleteNotificationChannel(4),
      "Could not delete notification channel",
    ],
    [
      "testing a notification channel",
      () => testNotificationChannel(4),
      "Could not test notification channel",
    ],
  ])("says so when %s fails", async (_name, act, words) => {
    refuseEverythingButTheToken();

    await expect(act()).rejects.toMatchObject({
      message: `${words}.`,
      status: 500,
    });
  });
});

describe("the requests that read back a file or a list", () => {
  it("downloads one automatic snapshot from its own address and returns the file", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValue(new Response("snapshot-bytes", { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);

    const blob = await fetchStoredConfigurationBackupBlob(12);

    expect(fetchMock.mock.calls[0][0]).toBe(
      `${configurationBackupsPath()}/12/download`,
    );
    await expect(blob.text()).resolves.toBe("snapshot-bytes");
  });
});

describe("deleting a notification channel", () => {
  function answerDelete(status: number) {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ csrf_token: "t" }), { status: 200 }),
      )
      .mockResolvedValueOnce(
        new Response(status === 204 ? null : "{}", { status }),
      );
    vi.stubGlobal("fetch", fetchMock);
  }

  it.each([[200], [204]])("accepts a %i answer", async (status) => {
    answerDelete(status);

    await expect(deleteNotificationChannel(5)).resolves.toBeUndefined();
  });

  it.each([[404], [500]])("refuses a %i answer", async (status) => {
    answerDelete(status);

    await expect(deleteNotificationChannel(5)).rejects.toMatchObject({
      status,
      message: "Could not delete notification channel.",
    });
  });
});
