import { renderHook } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { usePageTabsFit } from "./use-page-tabs-fit";

describe("fitting the page tabs before their row is drawn", () => {
  it("shows every tab while there is nothing to measure", () => {
    const { result } = renderHook(() =>
      usePageTabsFit({
        labels: ["Rules", "Schedule", "Cleanup"],
        selectedIndex: 1,
        enabled: true,
      }),
    );

    expect(result.current.fit).toEqual({ visible: [0, 1, 2], folded: [] });
  });
});
