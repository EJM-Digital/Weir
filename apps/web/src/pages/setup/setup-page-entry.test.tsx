import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import { adminMe } from "../../test/recorded-admin-session";
import { serveRecordedReplies } from "../../test/recorded-weir";
import {
  jsonAnswer,
  stubWeirApi,
  unexpectedProblem,
} from "../../test/stub-weir-api";
import { bootstrapStatusAfterSetup } from "../system/tabs/security/security-replies";
import { SetupPage } from "./setup-page";
import { bootstrapAllowed, noOneSignedIn } from "./setup-replies";

/**
 * What the first-run page does before it shows a form: it asks who is signed in and whether an account can still be
 * made, and sends the person on, or says it could not ask. The answers are the ones a running server gave.
 */

const ME = "GET /api/v1/auth/me";
const BOOTSTRAP_STATUS = "GET /api/v1/auth/bootstrap/status";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
});

function renderSetup() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, retryDelay: 0 },
      mutations: { retry: false },
    },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={["/setup"]}>
        <Routes>
          <Route path="/setup" element={<SetupPage />} />
          <Route path="/login" element={<p>Sign-in page</p>} />
          <Route path="/" element={<p>The app</p>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("the first-run page sends a person on", () => {
  it("to sign in when an account already exists", async () => {
    serveRecordedReplies({
      [ME]: noOneSignedIn,
      [BOOTSTRAP_STATUS]: bootstrapStatusAfterSetup,
    });

    renderSetup();

    expect(await screen.findByText("Sign-in page")).toBeInTheDocument();
  });

  it("to the app when they are already signed in, even though no account could be made", async () => {
    serveRecordedReplies({
      [ME]: adminMe,
      [BOOTSTRAP_STATUS]: bootstrapStatusAfterSetup,
    });

    renderSetup();

    expect(await screen.findByText("The app")).toBeInTheDocument();
  });

  it("to the app when they are already signed in, even while an account could still be made", async () => {
    serveRecordedReplies({
      [ME]: adminMe,
      [BOOTSTRAP_STATUS]: bootstrapAllowed,
    });

    renderSetup();

    expect(await screen.findByText("The app")).toBeInTheDocument();
  });
});

describe("the first-run page when it cannot ask whether an account can be made", () => {
  it("says Weir hit a problem, offers Reload, and a way back to sign in", async () => {
    vi.stubEnv("DEV", false);
    stubWeirApi({
      [ME]: () => jsonAnswer(JSON.parse(noOneSignedIn.body), 401),
      [BOOTSTRAP_STATUS]: unexpectedProblem,
    });

    renderSetup();

    expect(
      await screen.findByRole("heading", { name: "Weir hit a problem" }),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Reload" })).toBeInTheDocument();
    expect(
      screen.getByRole("link", { name: "Back to sign in" }),
    ).toHaveAttribute("href", "/login");
    expect(document.body).not.toHaveTextContent(/unexpected problem|500/);
  });

  it("says Weir cannot be reached when nothing answers", async () => {
    vi.stubEnv("DEV", false);
    stubWeirApi({
      [ME]: () => jsonAnswer(JSON.parse(noOneSignedIn.body), 401),
    });

    renderSetup();

    expect(
      await screen.findByRole("heading", { name: "Can't reach Weir" }),
    ).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Back to sign in" })).toBeVisible();
  });
});
