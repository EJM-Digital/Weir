import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import {
  serveRecordedReplies,
  type RecordedReply,
  type RecordedWeir,
} from "../../test/recorded-weir";
import { SetupPage } from "./setup-page";
import {
  accountRefused,
  bootstrapAllowed,
  csrfToken,
  noOneSignedIn,
} from "./setup-replies";
import {
  accountCreatedWithCodes,
  recoveryCodesConfirmed,
  setupCodes,
} from "../../test/recovery-replies";

const ME = "GET /api/v1/auth/me";
const BOOTSTRAP_STATUS = "GET /api/v1/auth/bootstrap/status";
const CSRF = "GET /api/v1/auth/csrf";
const BOOTSTRAP = "POST /api/v1/auth/bootstrap";
const CONFIRM = "POST /api/v1/auth/recovery-codes/confirm";

let weir: RecordedWeir | undefined;

afterEach(() => {
  cleanup();
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  vi.unstubAllGlobals();
});

function serve(bootstrap: RecordedReply | (() => Promise<RecordedReply>)) {
  weir = serveRecordedReplies({
    [ME]: noOneSignedIn,
    [BOOTSTRAP_STATUS]: bootstrapAllowed,
    [CSRF]: csrfToken,
    [BOOTSTRAP]: bootstrap,
    [CONFIRM]: recoveryCodesConfirmed,
  });
  return weir;
}

function renderSetup() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={["/setup"]}>
        <Routes>
          <Route path="/setup" element={<SetupPage />} />
          <Route path="/" element={<p>The app</p>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

async function fillIn(username: string) {
  fireEvent.change(await screen.findByTestId("setup-username"), {
    target: { value: username },
  });
  fireEvent.change(screen.getByTestId("setup-password"), {
    target: { value: "recorded-pass-123" },
  });
  fireEvent.change(screen.getByTestId("setup-confirm-password"), {
    target: { value: "recorded-pass-123" },
  });
}

describe("SetupPage's create account button", () => {
  it("creates the account with the trimmed username and a fresh secure token, then shows the recovery codes before anything else", async () => {
    const server = serve(accountCreatedWithCodes);
    renderSetup();

    await fillIn("  recorded-admin ");
    fireEvent.click(screen.getByTestId("setup-submit"));

    expect(await screen.findByTestId("setup-codes")).toBeInTheDocument();
    expect(screen.queryByText("The app")).toBeNull();
    expect(
      screen.getAllByRole("listitem").map((item) => item.textContent),
    ).toEqual(setupCodes);
    expect(server.sent("POST", "/api/v1/auth/bootstrap")).toEqual([
      expect.objectContaining({
        headers: expect.objectContaining({
          "content-type": "application/json",
        }),
        body: {
          username: "recorded-admin",
          password: "recorded-pass-123",
          csrf_token: "csrf.anonymous.atCoMQ.MtgwR55oCZyT9D3Ku7boJtmrTp8",
        },
      }),
    ]);
  });

  it("says Creating... and cannot be pressed again while the server is working", async () => {
    let finish: (reply: RecordedReply) => void = () => {};
    const server = serve(
      () => new Promise<RecordedReply>((resolve) => (finish = resolve)),
    );
    renderSetup();

    await fillIn("recorded-admin");
    fireEvent.click(screen.getByTestId("setup-submit"));

    const button = await screen.findByRole("button", { name: "Creating..." });
    expect(button).toBeDisabled();
    fireEvent.click(button);
    expect(server.sent("POST", "/api/v1/auth/bootstrap")).toHaveLength(1);

    finish(accountCreatedWithCodes);
    expect(await screen.findByTestId("setup-codes")).toBeInTheDocument();
  });

  it("waits on the codes until they are saved, however setup is read again meanwhile, then opens the app", async () => {
    const server = serve(accountCreatedWithCodes);
    renderSetup();
    await fillIn("recorded-admin");
    fireEvent.click(screen.getByTestId("setup-submit"));
    await screen.findByTestId("setup-codes");

    // Weir now reports the account as made, which on its own sends the page to sign-in.
    await vi.waitFor(() =>
      expect(
        server.sent("GET", "/api/v1/auth/bootstrap/status").length,
      ).toBeGreaterThan(1),
    );
    expect(screen.getByTestId("setup-codes")).toBeInTheDocument();
    const next = screen.getByTestId("recovery-codes-continue");
    expect(next).toBeDisabled();
    expect(server.sent("POST", "/api/v1/auth/recovery-codes/confirm")).toEqual(
      [],
    );

    fireEvent.click(screen.getByTestId("recovery-codes-saved"));
    fireEvent.click(next);

    expect(await screen.findByText("The app")).toBeInTheDocument();
    expect(
      server.sent("POST", "/api/v1/auth/recovery-codes/confirm"),
    ).toHaveLength(1);
  });

  it("shows the server's own reason when it refuses, and can be pressed again", async () => {
    serve(accountRefused);
    renderSetup();

    await fillIn("recorded-admin");
    fireEvent.click(screen.getByTestId("setup-submit"));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Bootstrap is not available: an admin user already exists.",
    );
    expect(screen.getByTestId("setup-submit")).toBeEnabled();
    expect(screen.getByTestId("setup-submit")).toHaveTextContent(
      "Create your account",
    );
  });

  it("sends nothing when the username is only spaces", async () => {
    const server = serve(accountCreatedWithCodes);
    renderSetup();

    await fillIn("   ");
    fireEvent.click(screen.getByTestId("setup-submit"));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Admin username is required.",
    );
    expect(server.sent("POST", "/api/v1/auth/bootstrap")).toEqual([]);
  });
});
