import { expect, it } from "vitest";

import {
  folderChainLines,
  type LibraryFolderChain,
} from "./library-folder-chain-api";

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
