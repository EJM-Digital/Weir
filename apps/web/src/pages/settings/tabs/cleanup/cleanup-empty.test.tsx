import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";

import { adminMe, csrfToken } from "../../../../test/recorded-admin-session";
import {
  serveRecordedReplies,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import { operatorSettings } from "../performance/performance-replies";
import { CleanupTab } from "./cleanup-tab";

let weir: RecordedWeir | undefined;

afterEach(() => {
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  vi.unstubAllGlobals();
});

it("says no cleanup jobs are available when the server lists none, and shows no table", async () => {
  weir = serveRecordedReplies({
    "GET /api/v1/auth/csrf": csrfToken,
    "GET /api/v1/auth/me": adminMe,
    "GET /api/v1/processing/operator-settings": operatorSettings,
    "GET /api/v1/processing/maintenance": {
      status: 200,
      body: '{"families":[]}',
    },
  });
  render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <CleanupTab />
    </QueryClientProvider>,
  );

  expect(
    await screen.findByText("No cleanup jobs are available on this instance."),
  ).toBeInTheDocument();
  expect(screen.queryByRole("table")).not.toBeInTheDocument();
  expect(screen.queryByTestId("settings-load-error")).not.toBeInTheDocument();
});
