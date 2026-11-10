import { describe, expect, it } from "vitest";

import {
  FOLDER_LINE_MEANING,
  READINESS_LABELS,
  READINESS_MEANING,
  folderChainLines,
  readinessOf,
  type FolderChainLine,
  type LibraryFolderChain,
} from "./library-folder-chain-api";

const line = (state: FolderChainLine["state"]): FolderChainLine =>
  ({ state, text: "x" }) as FolderChainLine;

describe("readinessOf", () => {
  it("needs attention whenever something is a problem, whatever the lines say", () => {
    expect(readinessOf(false, [])).toBe("needs_attention");
    expect(readinessOf(false, [line("ok")])).toBe("needs_attention");
    expect(readinessOf(false, [line("unverified")])).toBe("needs_attention");
  });

  it("is ready when nothing is a problem and every line is proven", () => {
    expect(readinessOf(true, [])).toBe("ready");
    expect(readinessOf(true, [line("ok"), line("note")])).toBe("ready");
  });

  it("is not verified as soon as one line is unverified, even among proven ones", () => {
    expect(readinessOf(true, [line("unverified")])).toBe("not_verified");
    expect(readinessOf(true, [line("ok"), line("unverified")])).toBe(
      "not_verified",
    );
    expect(readinessOf(true, [line("unverified"), line("ok")])).toBe(
      "not_verified",
    );
  });
});

describe("the words and meanings of a folder check", () => {
  it("label each readiness", () => {
    expect(READINESS_LABELS).toEqual({
      ready: "Ready",
      not_verified: "Not verified",
      needs_attention: "Needs attention",
    });
  });

  it("colour each readiness by what it means to the person", () => {
    expect(READINESS_MEANING).toEqual({
      ready: "done",
      not_verified: "attention",
      needs_attention: "attention",
    });
  });

  it("colour each line by its state", () => {
    expect(FOLDER_LINE_MEANING).toEqual({
      ok: "done",
      problem: "attention",
      note: "idle",
      unverified: "attention",
    });
  });
});

it("lists Weir's own lines first, then each media manager's, then each download client's", () => {
  const chain = {
    library_id: 2,
    ready: false,
    local: {
      ready: true,
      lines: [{ state: "ok", text: "Weir can read the watched folder." }],
    },
    managers: [
      { connection_id: 7, lines: [{ state: "note", text: "Manager line." }] },
      { connection_id: 8, lines: [{ state: "problem", text: "Other line." }] },
    ],
    download_clients: [
      {
        connection_id: 9,
        lines: [{ state: "unverified", text: "Client line." }],
      },
    ],
  } as unknown as LibraryFolderChain;

  expect(folderChainLines(chain).map((line) => line.text)).toEqual([
    "Weir can read the watched folder.",
    "Manager line.",
    "Other line.",
    "Client line.",
  ]);
});
