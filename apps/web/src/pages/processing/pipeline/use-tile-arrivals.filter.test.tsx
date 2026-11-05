import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor } from "@testing-library/react";
import { useRef } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { activityPage, passFinished } from "../../../test/dashboard-fixtures";
import { held } from "../../../test/held";
import { stubWeirApi, type StubbedRequest } from "../../../test/stub-weir-api";
import { useFinishedFiles } from "../use-finished-files";
import { TILE_KEY_ATTRIBUTE } from "./use-slide-neighbours";
import { useTileArrivals } from "./use-tile-arrivals";

const RECENT = "GET /api/v1/activity/recent";

function Shelf({ workflowId }: { workflowId: number }) {
  const { files } = useFinishedFiles("download", workflowId);
  const row = useRef<HTMLUListElement>(null);
  useTileArrivals(row, files.map((file) => file.relativePath).join("|"));
  return (
    <ul ref={row} aria-label="shelf">
      {files.map((file) => (
        <li
          key={file.relativePath}
          {...{ [TILE_KEY_ATTRIBUTE]: file.relativePath }}
        >
          {file.relativePath}
        </li>
      ))}
    </ul>
  );
}

let animated: string[];

beforeEach(() => {
  animated = [];
  Element.prototype.animate = vi.fn(function (this: Element) {
    animated.push(this.getAttribute(TILE_KEY_ATTRIBUTE) ?? "");
    return {} as Animation;
  });
});
afterEach(() => {
  Reflect.deleteProperty(Element.prototype, "animate");
  vi.unstubAllGlobals();
});

describe("the shelf when the workflow is changed", () => {
  it("keeps its tiles mounted until the new workflow's arrive, and only the new tile arrives", async () => {
    const next = held<unknown>();
    stubWeirApi({
      [RECENT]: (request: StubbedRequest) =>
        request.query.get("library_id") === "3"
          ? activityPage([passFinished(1, "A.mkv", { libraryId: 3 })])
          : next.promise,
    });
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    const view = (workflowId: number) => (
      <QueryClientProvider client={client}>
        <Shelf workflowId={workflowId} />
      </QueryClientProvider>
    );
    const { rerender } = render(view(3));
    const tile = await screen.findByText("A.mkv");
    animated.length = 0;

    rerender(view(4));

    expect(screen.getByText("A.mkv")).toBe(tile);
    next.resolve(
      activityPage([
        passFinished(2, "B.mkv", { libraryId: 4 }),
        passFinished(1, "A.mkv", { libraryId: 3 }),
      ]),
    );
    await act(async () => {
      await waitFor(() => expect(screen.getByText("B.mkv")).toBeVisible());
    });
    expect(screen.getByText("A.mkv")).toBe(tile);
    expect(animated).toEqual(["B.mkv"]);
  });
});
