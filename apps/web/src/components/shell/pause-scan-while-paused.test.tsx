import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";

import { PauseControl } from "./pause-control";

/** What /api/v1/pause answers on a fresh install (PauseApiTests in the contract suite). */
const running = {
  paused: false,
  paused_until: null,
  scan_while_paused: true,
  reason: "",
  in_flight_policy:
    "Work already running finishes. Pausing stops Weir starting anything new.",
};

type Sent = { url: string; method: string; body: Record<string, unknown> };

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });

/** A Weir that serves the pause and the sign-in the header needs, and records every pause change. */
function serverWithPause(savePause: (sent: Sent) => Response) {
  const saved: Sent[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      const method = init?.method ?? "GET";
      if (url === "/api/v1/auth/me") {
        return json({ user: { id: 1, username: "alice", role: "operator" } });
      }
      if (url === "/api/v1/auth/csrf") return json({ csrf_token: "token-1" });
      if (url === "/api/v1/pause" && method === "GET") return json(running);
      if (url === "/api/v1/pause" && method === "PUT") {
        const sent = {
          url,
          method,
          body: JSON.parse(String(init?.body)) as Record<string, unknown>,
        };
        saved.push(sent);
        return savePause(sent);
      }
      throw new Error(`unexpected request ${method} ${url}`);
    }),
  );
  return saved;
}

function renderHeader() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(<PauseControl />, {
    wrapper: ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    ),
  });
}

async function openMenu() {
  fireEvent.click(await screen.findByTestId("pause-open"));
  return screen.getByRole("checkbox", {
    name: "Keep looking for new files while paused",
  });
}

beforeEach(() => {
  vi.stubGlobal("fetch", undefined);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

it("saves the choice with the pause left off, and shows it once the server has answered", async () => {
  const saved = serverWithPause((sent) =>
    json({ ...running, scan_while_paused: sent.body.scan_while_paused }),
  );

  renderHeader();
  const checkbox = await openMenu();
  expect(checkbox).toBeChecked();

  fireEvent.click(checkbox);

  await waitFor(() => expect(checkbox).not.toBeChecked());
  expect(saved).toEqual([
    {
      url: "/api/v1/pause",
      method: "PUT",
      body: {
        paused: false,
        scan_while_paused: false,
        csrf_token: "token-1",
      },
    },
  ]);
  expect(screen.getByTestId("pause-menu")).toBeInTheDocument();
  expect(screen.queryByTestId("pause-alert")).not.toBeInTheDocument();
});

it("keeps the old choice and says it could not save when the server refuses", async () => {
  const saved = serverWithPause(() =>
    json({ detail: "Processing settings could not be stored." }, 503),
  );

  renderHeader();
  const checkbox = await openMenu();

  fireEvent.click(checkbox);

  expect(await screen.findByTestId("pause-alert")).toHaveTextContent(
    "Weir couldn't save that. Try again.",
  );
  expect(saved).toHaveLength(1);
  expect(checkbox).toBeChecked();
  expect(screen.getByTestId("pause-menu")).toBeInTheDocument();
});
