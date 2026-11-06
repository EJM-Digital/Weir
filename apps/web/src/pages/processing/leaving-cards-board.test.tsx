import { act, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { WithWorkflows } from "../../test/with-workflows";
import type { ProcessingFile } from "../../lib/processing/files-api";
import { useLeavingCards } from "./leaving-cards";
import { PipelineBoard } from "./pipeline/pipeline-board";
import { NOW, aFile, aWriting, lanesOf } from "./pipeline/pipeline-fixtures";

/** The page's wiring: the lanes and the file list come from one update, the ended cards follow from them. */
function Page({ files }: { files: ProcessingFile[] }) {
  const lanes = lanesOf(files);
  const leaving = useLeavingCards(
    lanes.waiting,
    lanes.working,
    lanes.handing,
    files,
  );
  return (
    <WithWorkflows>
      <MemoryRouter>
        <PipelineBoard
          lanes={lanes}
          leaving={leaving}
          filter="all"
          now={NOW}
          onOpen={vi.fn()}
        />
      </MemoryRouter>
    </WithWorkflows>
  );
}

beforeEach(() => {
  window.matchMedia = ((query: string) => ({
    matches: query.includes("prefers-reduced-motion"),
    media: query,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    addListener: () => undefined,
    removeListener: () => undefined,
    dispatchEvent: () => false,
    onchange: null,
  })) as typeof window.matchMedia;
});
afterEach(() => vi.restoreAllMocks());

describe("a card that ends keeps its element (#1383)", () => {
  it.each(["passed_through", "processed"] as const)(
    "the working card of a %s file becomes the Delivered card in the same element, never removed",
    (status) => {
      const { rerender } = render(<Page files={[aWriting(1)]} />);
      const working = screen.getByRole("button", { name: /Quiet Harbour/ });
      expect(working).not.toHaveClass("mm-pipe__card--delivered");

      const removed: Node[] = [];
      const watch = new MutationObserver((records) => {
        for (const record of records) removed.push(...record.removedNodes);
      });
      watch.observe(document.body, { childList: true, subtree: true });

      // The update that finishes the file: it leaves the lanes and the list says how it ended, together.
      act(() => rerender(<Page files={[aFile(1, status)]} />));
      removed.push(
        ...watch.takeRecords().flatMap((r) => Array.from(r.removedNodes)),
      );
      watch.disconnect();

      const delivered = screen.getByRole("button", { name: /Quiet Harbour/ });
      expect(delivered).toBe(working);
      expect(delivered).toHaveClass("mm-pipe__card--delivered");
      expect(removed).not.toContain(working);
    },
  );
});
