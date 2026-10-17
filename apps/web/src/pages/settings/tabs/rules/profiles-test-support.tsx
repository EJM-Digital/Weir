import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { expect } from "vitest";

import type { ProcessingRuleSet } from "../../../../lib/processing/rule-sets-api";
import { recordedBody, recordedResponse } from "../../../../test/recorded";
import {
  jsonAnswer,
  type StubbedRequest,
} from "../../../../test/stub-weir-api";
import { CSRF_TOKEN, stubWire } from "../../../../test/wire";
import { ProfilesTab } from "./profiles-tab";

const RULE_SETS = "/api/v1/processing/rule-sets";

/** The profiles a running Weir listed: "Movies rules", which a workflow uses, and "TV rules". */
export const RECORDED_PROFILES = recordedBody<ProcessingRuleSet[]>(
  `GET ${RULE_SETS}`,
);

/** The profile the tab opens first. */
export const STORED = RECORDED_PROFILES[0]!;

/** What a request carried besides its token. */
function sentRule(request: StubbedRequest): Record<string, unknown> {
  const { csrf_token: token, ...rest } = request.body as Record<
    string,
    unknown
  >;
  expect(token).toBe(CSRF_TOKEN);
  return rest;
}

/**
 * Stands in for the server at `fetch` for the Profiles tab. It lists the profiles it is given, and answers a save as the
 * server does: with the profile as saved, in the shape of the recorded answer to a save.
 */
export function serveProfiles(
  profiles: ProcessingRuleSet[] = RECORDED_PROFILES,
  options: { holdListAfterCreate?: boolean } = {},
) {
  let listed = profiles;
  let held: Promise<void> | null = null;
  let letListThrough = () => {};
  const saved = recordedBody<ProcessingRuleSet>(`PUT ${RULE_SETS}/3`);
  const api = stubWire({
    "GET /api/v1/auth/me": recordedResponse("GET /api/v1/auth/me"),
    "GET /api/v1/processing/libraries": recordedResponse(
      "GET /api/v1/processing/libraries",
    ),
    [`GET ${RULE_SETS}`]: async () => {
      await held;
      return listed;
    },
    [`PUT ${RULE_SETS}/${profiles[0]!.id}`]: (request: StubbedRequest) => {
      const profile = {
        ...saved,
        ...sentRule(request),
        id: profiles[0]!.id,
        used_by_library_count: profiles[0]!.used_by_library_count,
      };
      listed = listed.map((row) => (row.id === profile.id ? profile : row));
      return profile;
    },
    [`POST ${RULE_SETS}`]: (request: StubbedRequest) => {
      const profile = {
        ...saved,
        ...sentRule(request),
        id: 9,
        used_by_library_count: 0,
      };
      listed = [...listed, profile];
      if (options.holdListAfterCreate) {
        held = new Promise<void>((resolve) => {
          letListThrough = resolve;
        });
      }
      return jsonAnswer(profile, 201);
    },
  });
  return Object.assign(api, { letListThrough: () => letListThrough() });
}

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

/** Opens the tab and waits for the first profile to be on screen. */
export async function openProfiles() {
  render(<ProfilesTab />, { wrapper });
  await screen.findByRole("heading", { name: "Audio" });
}

/** What only the server works out about a profile; a save never sends it back. */
const SERVER_FIELDS = [
  "id",
  "used_by_library_count",
  "library_rescan_job_ids",
  "updated_at",
];

/** The saved profile as the page sends it back: every rule it was opened with, then these changes. */
export function expectedSave(
  changes: Record<string, unknown>,
  stored: ProcessingRuleSet = STORED,
) {
  const rules = Object.entries(stored).filter(
    ([key]) => !SERVER_FIELDS.includes(key),
  );
  return { ...Object.fromEntries(rules), ...changes };
}

/** The one request that saved the profile: its body without the token, once the save has been sent. */
export async function savedBody(
  api: ReturnType<typeof serveProfiles>,
  route = `PUT ${RULE_SETS}/${STORED.id}`,
) {
  await waitFor(() => expect(api.to(route)).toHaveLength(1));
  return sentRule(api.to(route)[0]!);
}
