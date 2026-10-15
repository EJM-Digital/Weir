import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import * as settingsQueries from "../../../../lib/settings/queries";
import {
  serveRecordedReplies,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import {
  csrfToken,
  downloadClientAddressRefused,
  sabnzbdListed,
  signedInAdmin,
} from "./connection-replies";
import { DownloadClientsSection } from "./download-clients-section";

const CONNECTIONS = "/api/v1/download-clients/connections";

const token = (JSON.parse(csrfToken.body) as { csrf_token: string }).csrf_token;

let weir: RecordedWeir | undefined;

beforeEach(() => {
  vi.spyOn(settingsQueries, "useAppSettingsQuery").mockReturnValue({
    data: undefined,
  } as ReturnType<typeof settingsQueries.useAppSettingsQuery>);
});

afterEach(() => {
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

describe("editing a download client", () => {
  it("says why the server refused the save, and keeps the form open with what was typed", async () => {
    weir = serveRecordedReplies({
      "GET /api/v1/auth/csrf": csrfToken,
      "GET /api/v1/auth/me": signedInAdmin,
      [`GET ${CONNECTIONS}`]: sabnzbdListed,
      [`PUT ${CONNECTIONS}/1`]: downloadClientAddressRefused,
    });
    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-edit"));

    fireEvent.change(screen.getByTestId("download-client-edit-base-url"), {
      target: { value: "not-a-url" },
    });
    fireEvent.click(screen.getByTestId("download-client-edit-save"));

    const form = screen.getByTestId("download-client-edit-form");
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "That address will not work: URL must be a valid http or https URL.",
    );
    expect(screen.getByTestId("download-client-edit-form")).toBe(form);
    expect(screen.getByTestId("download-client-edit-base-url")).toHaveValue(
      "not-a-url",
    );
    expect(screen.getByTestId("download-client-edit-save")).toBeEnabled();
    expect(weir.sent("PUT", `${CONNECTIONS}/1`).map((r) => r.body)).toEqual([
      { base_url: "not-a-url", csrf_token: token },
    ]);
  });

  it("says Weir cannot be reached when the request never arrives, and keeps the form open", async () => {
    weir = serveRecordedReplies({
      "GET /api/v1/auth/csrf": csrfToken,
      "GET /api/v1/auth/me": signedInAdmin,
      [`GET ${CONNECTIONS}`]: sabnzbdListed,
      [`PUT ${CONNECTIONS}/1`]: () =>
        Promise.reject(new TypeError("Failed to fetch")),
    });
    render(<DownloadClientsSection />, { wrapper });
    fireEvent.click(await screen.findByTestId("download-client-edit"));

    fireEvent.change(screen.getByTestId("download-client-edit-base-url"), {
      target: { value: "http://localhost:8086" },
    });
    fireEvent.click(screen.getByTestId("download-client-edit-save"));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Can't reach Weir. Check it's still running.",
    );
    expect(screen.getByTestId("download-client-edit-form")).toBeInTheDocument();
  });
});
