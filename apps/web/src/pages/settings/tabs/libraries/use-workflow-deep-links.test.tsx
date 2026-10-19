import { QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import { mediaManagerKeys } from "../../../../lib/media-managers/query-keys";
import { LibrariesTab } from "./libraries-tab";
import { library } from "./library-test-fixtures";
import {
  newTestClient,
  serveWorkflows,
  signedIn,
  type WorkflowServer,
} from "./workflow-server";

// The tab here is the real one, reading from a stand-in server at `fetch`. Pages elsewhere link into Workflows with the
// address alone; the file is about what the tab does with that address.

let server: WorkflowServer;

afterEach(() => {
  cleanup();
  expect(server.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

async function openAt(address: string) {
  const client = newTestClient();
  server = serveWorkflows({
    workflows: [library({ id: 4, name: "Kids" })],
    connections: [{ id: 5, kind: "deluno", name: "Deluno" }],
  });
  const router = createMemoryRouter(
    [{ path: "/setup/workflows", element: <LibrariesTab /> }],
    { initialEntries: [address] },
  );
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  await screen.findByTestId("processing-library-4");
  await signedIn(client);
  await waitFor(() =>
    expect(client.getQueryState(mediaManagerKeys.connections)?.status).toBe(
      "success",
    ),
  );
  return router;
}

describe("a link into Workflows", () => {
  it("opens the workflow's editor once and leaves the address, so a reload does not reopen it", async () => {
    const router = await openAt("/setup/workflows?edit=4");

    expect(await screen.findByTestId("processing-library-form")).toBeVisible();
    await waitFor(() => expect(router.state.location.search).toBe(""));
    expect(router.state.historyAction).toBe("REPLACE");
    expect(router.state.location.pathname).toBe("/setup/workflows");
  });

  it("opens the add choice once and leaves the address", async () => {
    const router = await openAt("/setup/workflows?addFrom=5");

    expect(await screen.findByTestId("add-workflow-choice")).toBeVisible();
    await waitFor(() => expect(router.state.location.search).toBe(""));
    expect(router.state.historyAction).toBe("REPLACE");
  });

  it.each(["edit=999", "edit=0", "edit=-4", "edit=abc", "edit=4.5", "edit="])(
    "opens nothing for ?%s",
    async (query) => {
      await openAt(`/setup/workflows?${query}`);

      expect(screen.queryByTestId("processing-library-form")).toBeNull();
      expect(screen.queryByTestId("add-workflow-choice")).toBeNull();
    },
  );
});
