import { afterEach, describe, expect, it, vi } from "vitest";
import {
  deleteNotificationChannel,
  fetchNetworkAccess,
  fetchServerLogDownload,
  fetchServerLogs,
  fetchUpdateStatus,
  serverLogsPath,
  networkAccessPath,
  putNetworkAccess,
  configurationBackupsPath,
  configurationBundlePath,
  securityOverviewPath,
  appSettingsPath,
  updateStatusPath,
} from "./settings-api";

afterEach(() => {
  vi.restoreAllMocks();
});

describe("suite settings API paths", () => {
  it("uses suite settings and security-overview routes", () => {
    expect(appSettingsPath()).toBe("/api/v1/suite/settings");
    expect(securityOverviewPath()).toBe("/api/v1/suite/security-overview");
    expect(configurationBundlePath()).toBe(
      "/api/v1/suite/configuration-bundle",
    );
    expect(configurationBackupsPath()).toBe(
      "/api/v1/suite/configuration-backups",
    );
    // One address per handler, so a 404 means the request is wrong rather than a missing alias.
    expect(updateStatusPath()).toBe("/api/v1/suite/update-status");
  });

  it("sends a CSRF header when deleting a notification channel", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ csrf_token: "csrf-test" }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        }),
      )
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    vi.stubGlobal("fetch", fetchMock);

    await deleteNotificationChannel(7);

    expect(fetchMock).toHaveBeenCalledTimes(2);
    const [, request] = fetchMock.mock.calls[1] as [string, RequestInit];
    expect(request.method).toBe("DELETE");
    expect(request.headers).toMatchObject({ "X-CSRF-Token": "csrf-test" });
  });

  it("saves who can reach Weir with a CSRF token in the body", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ csrf_token: "csrf-test" }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ state: "this_pc_only" }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        }),
      );
    vi.stubGlobal("fetch", fetchMock);

    await putNetworkAccess({ scope: "network" });

    const [path, request] = fetchMock.mock.calls[1] as [string, RequestInit];
    expect(path).toBe(networkAccessPath());
    expect(request.method).toBe("PUT");
    expect(JSON.parse(String(request.body))).toEqual({
      scope: "network",
      csrf_token: "csrf-test",
    });
  });
});

describe("reading the server's logs", () => {
  function answerWith(reply: Response) {
    const fetchMock = vi.fn().mockResolvedValue(reply);
    vi.stubGlobal("fetch", fetchMock);
    return fetchMock;
  }

  it.each([
    ["no filters", undefined, ""],
    ["empty filters", {}, ""],
    [
      "every filter",
      { level: "error", search: "disk", has_exception: true, limit: 50 },
      "?level=error&search=disk&has_exception=true&limit=50",
    ],
    [
      "a filter that is false or zero",
      { has_exception: false, limit: 0 },
      "?has_exception=false&limit=0",
    ],
    ["empty words", { level: "", search: "" }, ""],
  ])("asks with %s", async (_name, filters, query) => {
    const fetchMock = answerWith(new Response("{}", { status: 200 }));

    await fetchServerLogs(filters);

    expect(fetchMock.mock.calls[0][0]).toBe(`${serverLogsPath()}${query}`);
  });

  it.each([
    ["the logs", () => fetchServerLogs(), "Could not load logs"],
    [
      "the update status",
      () => fetchUpdateStatus(),
      "Could not check for updates",
    ],
    [
      "the network access",
      () => fetchNetworkAccess(),
      "Could not check network access",
    ],
    [
      "the log download",
      () => fetchServerLogDownload(),
      "Could not download the server log",
    ],
  ])("says why %s could not be read", async (_name, read, words) => {
    answerWith(
      new Response("{}", {
        status: 500,
        headers: { "Content-Type": "application/json" },
      }),
    );

    await expect(read()).rejects.toThrow(words);
  });

  it("reads the update status, the network access and the log file", async () => {
    answerWith(
      new Response(JSON.stringify({ status: "up_to_date" }), { status: 200 }),
    );
    await expect(fetchUpdateStatus()).resolves.toEqual({
      status: "up_to_date",
    });

    answerWith(
      new Response(JSON.stringify({ state: "this_pc_only" }), { status: 200 }),
    );
    await expect(fetchNetworkAccess()).resolves.toEqual({
      state: "this_pc_only",
    });

    answerWith(new Response("line one", { status: 200 }));
    await expect((await fetchServerLogDownload()).text()).resolves.toBe(
      "line one",
    );
  });
});
