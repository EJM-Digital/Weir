import { afterEach, describe, expect, it, vi } from "vitest";

import {
  CLEAN_START_FAILED,
  cleanLibraryFiles,
  fetchLibraryFiles,
  fetchLibraryOverview,
  libraryFileFiltersToParams,
  originalsFolderLabel,
  withStatusCounts,
  type LibraryOverview,
} from "./library-mode-api";

afterEach(() => {
  vi.unstubAllGlobals();
});

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });

/** The CSRF token request, then the answers to the calls the test makes. */
function answers(...replies: Response[]) {
  const fetchMock = vi
    .fn()
    .mockResolvedValueOnce(json(200, { csrf_token: "t" }));
  for (const reply of replies) fetchMock.mockResolvedValueOnce(reply);
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function sentBody(
  fetchMock: ReturnType<typeof vi.fn>,
): Record<string, unknown> {
  const [, request] = fetchMock.mock.calls[1] as [string, RequestInit];
  return JSON.parse(String(request.body)) as Record<string, unknown>;
}

function overviewWith(totals: Record<string, unknown>): LibraryOverview {
  return { totals } as unknown as LibraryOverview;
}

describe("withStatusCounts", () => {
  it("counts no file in any status when the server sends no per-status counts", () => {
    const { totals } = withStatusCounts(overviewWith({ files: 12 }));

    expect(totals.files).toBe(12);
    expect(totals.by_status).toEqual({
      needs_cleaning: 0,
      cleaning: 0,
      matches: 0,
      cant_clean_yet: 0,
      left_alone: 0,
    });
  });

  it("leaves the counts the server sent as they are", () => {
    const sent = overviewWith({
      files: 5,
      by_status: {
        needs_cleaning: 3,
        cleaning: 1,
        matches: 1,
        cant_clean_yet: 0,
        left_alone: 0,
      },
    });

    expect(withStatusCounts(sent)).toBe(sent);
  });
});

describe("originalsFolderLabel", () => {
  it.each([
    ["a folder of its own", "D:/Originals", "D:/Originals"],
    ["none set", "", "a .weir-originals folder inside each library folder"],
    [
      "only spaces",
      "   ",
      "a .weir-originals folder inside each library folder",
    ],
  ])("names the folder for %s", (_name, folder, label) => {
    expect(originalsFolderLabel(folder)).toBe(label);
  });
});

describe("the Files table's filters", () => {
  it.each([
    ["none", {}, ""],
    [
      "every one of them",
      {
        status: "needs_cleaning",
        manager: "Deluno",
        q: "alien",
        problem: "unreadable",
        facets: { video_codec: "hevc", resolution: "1080p" },
        sort: "size",
        direction: "desc",
        page: 3,
        page_size: 50,
      },
      "status=needs_cleaning&manager=Deluno&q=alien&problem=unreadable&video_codec=hevc&resolution=1080p&sort=size&direction=desc&page=3&page_size=50",
    ],
    ["the first page, which the server assumes", { page: 1 }, ""],
    ["a facet with no value", { facets: { audio: undefined } }, ""],
  ] as const)("are sent as the query for %s", (_name, filters, query) => {
    expect(
      libraryFileFiltersToParams(
        filters as Parameters<typeof libraryFileFiltersToParams>[0],
      ).toString(),
    ).toBe(query);
  });

  it.each([
    ["no filters", undefined, "/api/v1/processing/libraries/4/library-files"],
    [
      "filters",
      { q: "alien" },
      "/api/v1/processing/libraries/4/library-files?q=alien",
    ],
  ])("read the files with %s", async (_name, filters, path) => {
    const fetchMock = vi.fn().mockResolvedValue(json(200, { items: [] }));
    vi.stubGlobal("fetch", fetchMock);

    await fetchLibraryFiles(4, filters);

    expect(fetchMock.mock.calls[0][0]).toBe(path);
  });

  it("say why the files could not be read", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json(500, {})));

    await expect(fetchLibraryFiles(4)).rejects.toThrow(
      "Could not load this workflow's files",
    );
  });
});

describe("the overview", () => {
  it("counts no file in any status when the server sends no counts", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(json(200, { totals: { files: 2 } })),
    );

    const overview = await fetchLibraryOverview(4);

    expect(overview.totals.by_status.matches).toBe(0);
  });

  it("says why it could not be read", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json(500, {})));

    await expect(fetchLibraryOverview(4)).rejects.toThrow(
      "Could not load this workflow's overview",
    );
  });
});

describe("cleaning files", () => {
  it("sends the files and the choice, and hands back what was cleaned", async () => {
    const fetchMock = answers(json(200, { cleaned: 2 }));

    const result = await cleanLibraryFiles(4, ["a.mkv", "b.mkv"], false);

    expect(result).toEqual({ kind: "cleaned", cleaned: 2 });
    expect(sentBody(fetchMock)).toMatchObject({
      paths: ["a.mkv", "b.mkv"],
      confirm_final_removal: false,
    });
    expect(sentBody(fetchMock)).not.toHaveProperty("manual_plan");
  });

  it.each([
    ["with the size the file had", 1234, { expected_size_bytes: 1234 }],
    ["without a size", undefined, {}],
  ])("sends your own choice of tracks %s", async (_name, size, extra) => {
    const fetchMock = answers(json(200, {}));
    const keep = [{ index: 0, default: true, forced: false }];

    await cleanLibraryFiles(4, ["a.mkv"], true, {
      keep,
      order: [0],
      expected_size_bytes: size,
    });

    const body = sentBody(fetchMock);
    expect(body.manual_plan).toEqual({ keep, order: [0] });
    expect(body.expected_size_bytes).toBe(
      "expected_size_bytes" in extra ? 1234 : undefined,
    );
  });

  it("hands back what removal needs confirming, in the server's words", async () => {
    answers(
      json(400, {
        error: "confirm_final_removal_required",
        detail: "2 files, 5 tracks will be removed.",
        files_count: 2,
        tracks_count: 5,
        estimated_bytes_saved: 900,
        warnings: ["a.mkv is still seeding"],
      }),
    );

    await expect(cleanLibraryFiles(4, ["a.mkv"], false)).resolves.toEqual({
      kind: "confirmation_required",
      detail: "2 files, 5 tracks will be removed.",
      files_count: 2,
      tracks_count: 5,
      estimated_bytes_saved: 900,
      warnings: ["a.mkv is still seeding"],
    });
  });

  it("counts nothing the server did not count when it asks for confirmation", async () => {
    answers(json(400, { error: "confirm_final_removal_required" }));

    await expect(cleanLibraryFiles(4, ["a.mkv"], false)).resolves.toEqual({
      kind: "confirmation_required",
      detail: "",
      files_count: 0,
      tracks_count: 0,
      estimated_bytes_saved: 0,
      warnings: undefined,
    });
  });

  it("gives the reason when the server refuses a clean with one of its own", async () => {
    answers(json(400, { detail: "Choose at least one file to clean." }));

    await expect(cleanLibraryFiles(4, [], false)).rejects.toThrow(
      "Choose at least one file to clean.",
    );
  });

  it.each([
    ["another refusal", json(400, { error: "something_else" })],
    ["a refusal with no reason", json(500, {})],
    [
      "a refusal that is not JSON",
      new Response("Bad Request", { status: 400 }),
    ],
  ])("says the clean could not start on %s", async (_name, reply) => {
    answers(reply);

    await expect(cleanLibraryFiles(4, ["a.mkv"], false)).rejects.toThrow(
      CLEAN_START_FAILED,
    );
  });
});
