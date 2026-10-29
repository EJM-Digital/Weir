import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";

import type { RecordedReply } from "./recorded-weir";
import {
  appSettings,
  homeNetworkOff,
} from "../pages/system/tabs/security/security-replies";

/** Every table of times asks for the time zone Weir shows them in. */
export const APP_SETTINGS = { "GET /api/v1/suite/settings": appSettings };

/** Skipping sign-in on the home network is off, which is what the Security tab asks about first. */
export const HOME_NETWORK_OFF = {
  "GET /api/v1/suite/home-network-sign-in": homeNetworkOff,
};

/** A client that asks again on every mount and never retries, so a test sees exactly the requests the page makes. */
export function testClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: 0 },
      mutations: { retry: false },
    },
  });
}

export function renderSecurity(ui: ReactNode, client = testClient()) {
  return {
    client,
    ...render(
      <QueryClientProvider client={client}>
        <MemoryRouter>{ui}</MemoryRouter>
      </QueryClientProvider>,
    ),
  };
}

/** A reply the test lets go of when it chooses, so the page can be looked at while the request is still out. */
export function heldReply(reply: RecordedReply) {
  let release: () => void = () => undefined;
  const answered = new Promise<RecordedReply>((resolve) => {
    release = () => resolve(reply);
  });
  return { answer: () => answered, release };
}
