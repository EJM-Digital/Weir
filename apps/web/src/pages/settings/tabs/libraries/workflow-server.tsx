import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { waitFor } from "@testing-library/react";
import { expect } from "vitest";

import { authKeys } from "../../../../lib/auth/query-keys";
import type { MediaManagerConnection } from "../../../../lib/media-managers/media-managers-api";
import type { ProcessingLibrary } from "../../../../lib/processing/libraries-api";
import type { LibraryFolderChain } from "../../../../lib/processing/library-folder-chain-api";
import type { ProcessingRuleSet } from "../../../../lib/processing/rule-sets-api";
import {
  jsonAnswer,
  savedFields,
  stubWeirApi,
  type StubbedApi,
  type StubbedRequest,
} from "../../../../test/stub-weir-api";

export const LIBRARIES = "/api/v1/processing/libraries";
export const CSRF_TOKEN = "csrf-token-for-the-test";

const librariesRoute = `GET ${LIBRARIES}`;
export const updateRoute = (id: number) => `PUT ${LIBRARIES}/${id}`;
export const unlinkRoute = (id: number) => `POST ${LIBRARIES}/${id}/unlink`;
export const folderChainRoute = (id: number) =>
  `GET ${LIBRARIES}/${id}/folder-chain`;
export const managerSetupRoute = "GET /api/v1/processing/manager-setup";

/** A chain with nothing to report, for a test that is about when the chain is asked for, not what it says. */
export function readyChain(
  libraryId: number,
  text: string,
): LibraryFolderChain {
  return {
    library_id: libraryId,
    local: { ready: true, lines: [{ state: "ok", text }] },
    managers: [],
    download_clients: [],
    ready: true,
  };
}

/** What a request the test is not about gets: the server could not answer it just now. */
const unavailable = () =>
  jsonAnswer({ detail: "This test does not ask for that." }, 503);

export type WorkflowServer = StubbedApi & {
  /** The workflows as the server holds them now. */
  workflows: ProcessingLibrary[];
  /** Changes a workflow on the server, as a second browser or a media manager's sync would. */
  change: (id: number, fields: Partial<ProcessingLibrary>) => void;
};

/**
 * A Weir server holding workflows. A page reads them with `GET`, a save with `PUT` changes what is held, and Unlink clears
 * the link, so what a page shows after a change is what the server would show. Requests the editor makes that a test is not
 * about are answered as unavailable.
 */
export function serveWorkflows({
  workflows,
  role = "operator",
  ruleSets = [],
  connections = [],
  chains = {},
  extra = {},
}: {
  workflows: ProcessingLibrary[];
  role?: "operator" | "viewer";
  ruleSets?: Pick<ProcessingRuleSet, "id" | "name">[];
  connections?: Pick<MediaManagerConnection, "id" | "kind" | "name">[];
  /** What a workflow's folder chain says, by workflow id; asked again on every request, so a test can change it. */
  chains?: Record<number, () => LibraryFolderChain>;
  extra?: Record<string, unknown | ((request: StubbedRequest) => unknown)>;
}): WorkflowServer {
  const held = workflows.map((workflow) => ({ ...workflow }));
  const find = (id: number) => held.find((workflow) => workflow.id === id)!;
  const change = (id: number, fields: Partial<ProcessingLibrary>) =>
    void Object.assign(find(id), fields);
  const perWorkflow = Object.fromEntries(
    held.flatMap(({ id }) => [
      [
        updateRoute(id),
        (request: StubbedRequest) => {
          change(id, savedFields(request) as Partial<ProcessingLibrary>);
          return find(id);
        },
      ],
      [
        unlinkRoute(id),
        () => {
          change(id, {
            discovered_from_connection_id: null,
            discovered_library_key: null,
          });
          return find(id);
        },
      ],
      [
        folderChainRoute(id),
        () => (chains[id] ?? (() => readyChain(id, "Weir can read it.")))(),
      ],
    ]),
  );
  const api = stubWeirApi({
    "GET /api/v1/auth/me": {
      user: { id: 1, role, username: role, app_theme: null },
    },
    "GET /api/v1/auth/csrf": { csrf_token: CSRF_TOKEN },
    [librariesRoute]: () => held.map((workflow) => ({ ...workflow })),
    "GET /api/v1/processing/rule-sets": ruleSets,
    "GET /api/v1/media-managers/connections": connections,
    "GET /api/v1/processing/reject-support": unavailable,
    "GET /api/v1/download-clients/suggestions": unavailable,
    "GET /api/v1/processing/operator-settings": unavailable,
    [managerSetupRoute]: unavailable,
    ...perWorkflow,
    ...extra,
  });
  return Object.assign(api, { workflows: held, change });
}

/** The tab under a client the test holds, so it can say the server changed something the way a live update does. */
export function workflowTabWrapper(client: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <MemoryRouter>
        <QueryClientProvider client={client}>{children}</QueryClientProvider>
      </MemoryRouter>
    );
  };
}

export function newTestClient(): QueryClient {
  return new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
}

/** Waits for the session to say who is signed in: until then every control that changes something is disabled. */
export async function signedIn(client: QueryClient) {
  await waitFor(() =>
    expect(client.getQueryState(authKeys.me)?.status).toBe("success"),
  );
}
