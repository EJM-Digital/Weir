import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import * as settingsQueries from "../../../../lib/settings/queries";
import {
  serveRecordedReplies,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import {
  WEBHOOK_SECRET,
  csrfToken,
  delunoAdded,
  delunoListedWithSecret,
  delunoListedWithoutSecret,
  delunoSecretCreated,
  noConnections,
  sabnzbdAdded,
  sabnzbdListed,
} from "./connection-replies";
import { DownloadClientsSection } from "./download-clients-section";
import { MediaManagersTab } from "./media-managers-tab";
import { stubMediaManagersTabNeighbours } from "./stub-media-managers-tab-neighbours";

const CONSOLE_METHODS = [
  "debug",
  "error",
  "info",
  "log",
  "trace",
  "warn",
] as const;

let weir: RecordedWeir | undefined;
let consoleOutput: string[];

beforeEach(() => {
  consoleOutput = [];
  for (const method of CONSOLE_METHODS) {
    vi.spyOn(console, method).mockImplementation((...args: unknown[]) => {
      consoleOutput.push(args.map((arg) => String(arg)).join(" "));
    });
  }
  localStorage.clear();
  sessionStorage.clear();
});

afterEach(() => {
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return (
    <MemoryRouter>
      <QueryClientProvider client={qc}>{children}</QueryClientProvider>
    </MemoryRouter>
  );
}

/** Everything this browser tab keeps besides the page itself, which a secret must not be found in. */
function everythingKept(): string {
  const stored = (storage: Storage) =>
    Array.from({ length: storage.length }, (_, i) => {
      const key = storage.key(i) ?? "";
      return `${key}=${storage.getItem(key)}`;
    });
  return [
    ...stored(localStorage),
    ...stored(sessionStorage),
    ...consoleOutput,
    document.cookie,
  ].join("\n");
}

/** Every address the page asked for, with the body it sent, as text. */
function requestsAsText(server: RecordedWeir): string[] {
  return server.requests.map(
    (request) =>
      `${request.method} ${request.path} ${JSON.stringify(request.body ?? "")}`,
  );
}

describe("the Connections screens keep secrets to themselves", () => {
  it("never logs, stores or sends on a media manager's webhook secret or API key", async () => {
    const typedKey = "typed-deluno-api-key";
    weir = serveRecordedReplies({
      "GET /api/v1/auth/csrf": csrfToken,
      "GET /api/v1/media-managers/connections": [
        noConnections,
        delunoListedWithoutSecret,
        delunoListedWithSecret,
      ],
      "POST /api/v1/media-managers/connections": delunoAdded,
      "POST /api/v1/media-managers/connections/1/webhook-secret":
        delunoSecretCreated,
    });
    stubMediaManagersTabNeighbours();

    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByTestId("media-manager-add"));
    fireEvent.change(screen.getByTestId("media-manager-base-url"), {
      target: { value: "http://localhost:5099" },
    });
    fireEvent.change(screen.getByTestId("media-manager-api-key"), {
      target: { value: typedKey },
    });
    fireEvent.click(screen.getByTestId("media-manager-save"));
    fireEvent.click(
      await screen.findByTestId("media-manager-new-secret-create"),
    );

    expect(await screen.findByTestId("media-manager-secret")).toHaveTextContent(
      WEBHOOK_SECRET,
    );

    const kept = everythingKept();
    expect(kept).not.toContain(WEBHOOK_SECRET);
    expect(kept).not.toContain(typedKey);

    const sent = requestsAsText(weir);
    expect(sent.filter((line) => line.includes(typedKey))).toEqual([
      expect.stringMatching(/^POST \/api\/v1\/media-managers\/connections \{/),
    ]);
    expect(sent.filter((line) => line.includes(WEBHOOK_SECRET))).toEqual([]);
  });

  it("never logs, stores or sends on a download client's API key beyond saving it", async () => {
    const typedKey = "typed-sabnzbd-api-key";
    weir = serveRecordedReplies({
      "GET /api/v1/auth/csrf": csrfToken,
      "GET /api/v1/download-clients/connections": [
        noConnections,
        sabnzbdListed,
      ],
      "POST /api/v1/download-clients/connections": sabnzbdAdded,
    });
    vi.spyOn(settingsQueries, "useAppSettingsQuery").mockReturnValue({
      data: undefined,
    } as ReturnType<typeof settingsQueries.useAppSettingsQuery>);

    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-add"));
    fireEvent.change(screen.getByTestId("download-client-base-url"), {
      target: { value: "http://localhost:8085" },
    });
    fireEvent.change(screen.getByTestId("download-client-api-key"), {
      target: { value: typedKey },
    });
    fireEvent.click(screen.getByTestId("download-client-save"));

    expect(await screen.findByText("SABnzbd on localhost")).toBeInTheDocument();

    expect(everythingKept()).not.toContain(typedKey);
    expect(document.body.innerHTML).not.toContain(typedKey);
    expect(
      requestsAsText(weir).filter((line) => line.includes(typedKey)),
    ).toEqual([
      expect.stringMatching(
        /^POST \/api\/v1\/download-clients\/connections \{/,
      ),
    ]);
  });
});
