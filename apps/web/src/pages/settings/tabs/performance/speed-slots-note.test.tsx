import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, expect, it, vi } from "vitest";

import { adminMe } from "../../../../test/recorded-admin-session";
import {
  serveRecordedReplies,
  type RecordedReply,
} from "../../../../test/recorded-weir";
import {
  filesAtOnceCappedBySlots,
  filesAtOnceNotCapped,
  operatorSettings,
  operatorSettingsAtTen,
} from "./performance-replies";
import { SpeedTab } from "./speed-tab";

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function showSpeed(settings: RecordedReply, filesAtOnce: RecordedReply) {
  serveRecordedReplies({
    "GET /api/v1/auth/me": adminMe,
    "GET /api/v1/processing/operator-settings": settings,
    "GET /api/v1/processing/files-at-once": filesAtOnce,
  });
  render(<SpeedTab />, { wrapper });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

it("says why Files at once runs fewer than it asks for, in the words the server gave", async () => {
  showSpeed(operatorSettingsAtTen, filesAtOnceCappedBySlots);

  const note = await screen.findByTestId("processing-files-at-once-note");
  expect(note).toHaveTextContent(
    "This Weir was started with 2 worker slots, so it runs at most 2 at once. Set WEIR_PROCESSING_WORKER_COUNT higher and restart Weir to use more.",
  );
  expect(
    screen.getByTestId("processing-files-at-once-readout"),
  ).toHaveTextContent("0 running now. Nothing is waiting.");
});

it("says nothing about slots while the server has enough of them", async () => {
  showSpeed(operatorSettings, filesAtOnceNotCapped);

  expect(
    await screen.findByTestId("processing-files-at-once-readout"),
  ).toBeInTheDocument();
  expect(
    screen.queryByTestId("processing-files-at-once-note"),
  ).not.toBeInTheDocument();
});
