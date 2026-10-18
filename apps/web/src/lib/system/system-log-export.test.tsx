import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { systemReplies } from "../../test/recorded-system";
import {
  serveRecordedReplies,
  type RecordedReply,
} from "../../test/recorded-weir";
import { fetchMediaTools, useMediaToolsQuery } from "./media-tools";
import { fetchSystemLogExport } from "./system-log-api";

const EXPORT = "GET /api/v1/system/log/export";
const MEDIA_TOOLS = "GET /api/v1/system/media-tools";

/** What the server sends for a log export: the rows, named by a Content-Disposition of its own form (SystemLogWire). */
function exported(text: string, disposition: string | null): RecordedReply {
  return {
    status: 200,
    body: text,
    headers: disposition ? { "Content-Disposition": disposition } : {},
  };
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe("exporting the log", () => {
  it("asks for a spreadsheet as text/csv and names the file as the server did", async () => {
    const weir = serveRecordedReplies({
      [EXPORT]: exported(
        "At,Level\n",
        'attachment; filename="weir-log-20261015-190000.csv"',
      ),
    });

    const file = await fetchSystemLogExport("csv", { source: ["job"] });

    expect(file.filename).toBe("weir-log-20261015-190000.csv");
    expect(await file.blob.text()).toBe("At,Level\n");
    expect(weir.requests[0].headers.accept).toBe("text/csv");
    expect(weir.requests[0].path).toBe("/api/v1/system/log/export");
  });

  it("asks for JSON as application/json", async () => {
    const weir = serveRecordedReplies({
      [EXPORT]: exported(
        "[]",
        'attachment; filename="weir-log-20261015-190000.json"',
      ),
    });

    const file = await fetchSystemLogExport("json", {});

    expect(file.filename).toBe("weir-log-20261015-190000.json");
    expect(weir.requests[0].headers.accept).toBe("application/json");
  });

  it.each([
    ["csv", null],
    ["json", ""],
    ["json", "attachment"],
  ] as const)(
    "names a %s file weir-log.%s when the server's disposition (%s) gives no name",
    async (format, disposition) => {
      serveRecordedReplies({ [EXPORT]: exported("x", disposition) });

      const file = await fetchSystemLogExport(format, {});

      expect(file.filename).toBe(`weir-log.${format}`);
    },
  );

  it("says the log could not be exported when the server fails", async () => {
    serveRecordedReplies({ [EXPORT]: systemReplies.settingsPutFails });

    await expect(fetchSystemLogExport("csv", {})).rejects.toThrow(
      "Could not export the log",
    );
  });
});

describe("the media tools Weir found", () => {
  function wrapper(client: QueryClient) {
    function Provider({ children }: { children: ReactNode }) {
      return (
        <QueryClientProvider client={client}>{children}</QueryClientProvider>
      );
    }
    return Provider;
  }

  it("reads their version lines from the media-tools route", async () => {
    const weir = serveRecordedReplies({
      [MEDIA_TOOLS]: systemReplies.mediaTools,
    });

    const tools = await fetchMediaTools();

    expect(tools).toEqual(JSON.parse(systemReplies.mediaTools.body));
    expect(weir.requests.map((r) => `${r.method} ${r.path}`)).toEqual([
      MEDIA_TOOLS,
    ]);
  });

  it("says the tools could not be read when the server fails", async () => {
    serveRecordedReplies({ [MEDIA_TOOLS]: systemReplies.settingsPutFails });

    await expect(fetchMediaTools()).rejects.toThrow(
      "Could not read Weir's tools",
    );
  });

  it("reads them once and trusts the answer for five minutes, then reads again", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date("2026-10-15T12:00:00Z"));
    const weir = serveRecordedReplies({
      [MEDIA_TOOLS]: systemReplies.mediaTools,
    });
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    const open = async () => {
      const view = renderHook(() => useMediaToolsQuery(), {
        wrapper: wrapper(client),
      });
      await waitFor(() => expect(view.result.current.data).toBeDefined());
      view.unmount();
    };

    await open();
    vi.setSystemTime(new Date("2026-10-15T12:04:00Z"));
    await open();
    expect(weir.sent("GET", "/api/v1/system/media-tools")).toHaveLength(1);

    vi.setSystemTime(new Date("2026-10-15T12:06:00Z"));
    await open();
    await waitFor(() =>
      expect(weir.sent("GET", "/api/v1/system/media-tools")).toHaveLength(2),
    );
  });
});
