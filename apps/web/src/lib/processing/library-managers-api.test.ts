import { afterEach, expect, it, vi } from "vitest";

import { importDiscoveredProcessingLibraries } from "./library-managers-api";
import { processingLibrariesPath } from "./libraries-api";

afterEach(() => vi.unstubAllGlobals());

it("asks for the chosen libraries by key and hands back the workflows it made", async () => {
  const json = (body: unknown) =>
    new Response(JSON.stringify(body), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    });
  const fetchMock = vi
    .fn()
    .mockResolvedValueOnce(json({ csrf_token: "csrf-test" }))
    .mockResolvedValueOnce(json([{ id: 5, name: "Films" }]));
  vi.stubGlobal("fetch", fetchMock);

  const imported = await importDiscoveredProcessingLibraries(3, ["movies"]);

  expect(imported).toEqual([{ id: 5, name: "Films" }]);
  const [path, request] = fetchMock.mock.calls[1] as [string, RequestInit];
  expect(path).toBe(`${processingLibrariesPath()}/discover/3/import`);
  expect(request.method).toBe("POST");
  expect(JSON.parse(String(request.body))).toMatchObject({ keys: ["movies"] });
});
