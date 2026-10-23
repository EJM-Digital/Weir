import { afterEach, expect, it, vi } from "vitest";

import { jsonAnswer } from "../../test/stub-weir-api";
import { stubWire } from "../../test/wire";
import { fetchCurrentSession } from "./auth-api";

afterEach(() => vi.unstubAllGlobals());

it("has no current session for a browser that is not signed in", async () => {
  stubWire({
    "GET /api/v1/auth/session": () =>
      jsonAnswer({ detail: "Not signed in." }, 401),
  });

  await expect(fetchCurrentSession()).resolves.toBeNull();
});
