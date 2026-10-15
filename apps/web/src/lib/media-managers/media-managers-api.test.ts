import { afterEach, describe, expect, it, vi } from "vitest";

import {
  deleteMediaManagerConnection,
  giveUpMediaManagerReports,
  unlinkMediaManagerConnectionWorkflows,
} from "./media-managers-api";

afterEach(() => {
  vi.restoreAllMocks();
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

const IN_USE = {
  code: "connection_in_use",
  detail:
    "Movies still comes from Deluno on nas. Nothing was removed. Before removing it, unlink or remove that workflow.",
  workflows: [{ id: 1, name: "Movies" }],
  handoffs_in_progress: 0,
  reports: [],
};

describe("removing a connection", () => {
  it("is removed when the server answers 204", async () => {
    const fetchMock = answers(new Response(null, { status: 204 }));

    await expect(deleteMediaManagerConnection(4)).resolves.toBeUndefined();

    const [path, request] = fetchMock.mock.calls[1] as [string, RequestInit];
    expect(path).toBe("/api/v1/media-managers/connections/4");
    expect(request.method).toBe("DELETE");
  });

  it("hands back what the connection is still needed for when the server refuses with connection_in_use", async () => {
    answers(json(409, IN_USE));

    await expect(deleteMediaManagerConnection(4)).resolves.toEqual(IN_USE);
  });

  it("throws the server's sentence for any other refusal, a 409 with another code included", async () => {
    answers(json(409, { code: "something_else", detail: "Not now." }));

    await expect(deleteMediaManagerConnection(4)).rejects.toThrow("Not now.");
  });

  it("throws for a 409 whose body is not JSON", async () => {
    answers(new Response("busy", { status: 409 }));

    await expect(deleteMediaManagerConnection(4)).rejects.toThrow();
  });

  it("throws a plain sentence for a failure Weir did not foresee", async () => {
    answers(
      json(500, { detail: "Weir hit a problem.", code: "internal_error" }),
    );

    await expect(deleteMediaManagerConnection(4)).rejects.toThrow(
      "Could not remove that media manager.",
    );
  });
});

describe("unlinking a connection's workflows", () => {
  it("posts to the connection's unlink route and returns the workflows unlinked", async () => {
    const fetchMock = answers(
      json(200, { workflows: [{ id: 1, name: "Movies" }] }),
    );

    await expect(unlinkMediaManagerConnectionWorkflows(4)).resolves.toEqual({
      workflows: [{ id: 1, name: "Movies" }],
    });

    const [path, request] = fetchMock.mock.calls[1] as [string, RequestInit];
    expect(path).toBe("/api/v1/media-managers/connections/4/unlink-workflows");
    expect(request.method).toBe("POST");
  });

  it("throws the server's sentence when a hand-off is still in progress", async () => {
    answers(
      json(409, {
        code: "handoffs_in_progress",
        detail:
          "Weir is still working on 1 hand-off from Deluno on nas, so its workflows stay linked. Nothing was changed. Let the hand-off finish or cancel it first.",
      }),
    );

    await expect(unlinkMediaManagerConnectionWorkflows(4)).rejects.toThrow(
      "Let the hand-off finish or cancel it first.",
    );
  });
});

describe("giving up on the reports owed to a connection", () => {
  it("posts to the connection's give-up route and returns what was dropped", async () => {
    const fetchMock = answers(
      json(200, { reports: [{ handoff_id: "h1", name: "film.mkv" }] }),
    );

    await expect(giveUpMediaManagerReports(4)).resolves.toEqual({
      reports: [{ handoff_id: "h1", name: "film.mkv" }],
    });

    const [path, request] = fetchMock.mock.calls[1] as [string, RequestInit];
    expect(path).toBe("/api/v1/media-managers/connections/4/give-up-reports");
    expect(request.method).toBe("POST");
  });

  it("throws the server's sentence when it refuses", async () => {
    answers(
      json(404, { detail: "That media manager connection does not exist." }),
    );

    await expect(giveUpMediaManagerReports(4)).rejects.toThrow(
      "That media manager connection does not exist.",
    );
  });
});
