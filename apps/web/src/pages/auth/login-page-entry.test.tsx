import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import { serveRecordedReplies } from "../../test/recorded-weir";
import {
  jsonAnswer,
  stubWeirApi,
  unexpectedProblem,
} from "../../test/stub-weir-api";
import {
  bootstrapStatusAfterSetup,
  me,
} from "../system/tabs/security/security-replies";
import { noOneSignedIn } from "../setup/setup-replies";
import { LoginPage } from "./login-page";

/**
 * What the sign-in page does before and around its form: it asks who is signed in and whether an account exists, says so
 * when it cannot ask, and holds back a form with nothing typed in it. The answers are the ones a running server gave.
 */

const ME = "GET /api/v1/auth/me";
const BOOTSTRAP_STATUS = "GET /api/v1/auth/bootstrap/status";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
});

function renderLogin() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, retryDelay: 0 },
      mutations: { retry: false },
    },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={["/login"]}>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const signedOut = () => jsonAnswer(JSON.parse(noOneSignedIn.body), 401);

describe("the sign-in page when it cannot ask", () => {
  it("says Weir hit a problem when who is signed in cannot be read, and Reload loads the page again", async () => {
    vi.stubEnv("DEV", false);
    const reload = vi.fn();
    vi.stubGlobal("location", { ...window.location, reload });
    stubWeirApi({
      [ME]: unexpectedProblem,
      [BOOTSTRAP_STATUS]: jsonAnswer(
        JSON.parse(bootstrapStatusAfterSetup.body),
      ),
    });

    renderLogin();

    expect(
      await screen.findByRole("heading", { name: "Weir hit a problem" }),
    ).toBeInTheDocument();
    expect(screen.queryByTestId("login-form")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Reload" }));
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it("says Weir cannot be reached when whether an account exists cannot be read", async () => {
    vi.stubEnv("DEV", false);
    stubWeirApi({ [ME]: signedOut });

    renderLogin();

    expect(
      await screen.findByRole("heading", { name: "Can't reach Weir" }),
    ).toBeInTheDocument();
    expect(screen.queryByTestId("login-form")).not.toBeInTheDocument();
  });
});

describe("the sign-in form with nothing typed in it", () => {
  function serveSignedOut() {
    return serveRecordedReplies({
      [ME]: noOneSignedIn,
      [BOOTSTRAP_STATUS]: bootstrapStatusAfterSetup,
    });
  }

  it("will not send an empty name and password", async () => {
    const server = serveSignedOut();
    renderLogin();
    await screen.findByTestId("login-form");

    fireEvent.click(screen.getByTestId("login-submit"));

    expect(screen.getByTestId("login-username")).toBeRequired();
    expect(screen.getByTestId("login-password")).toBeRequired();
    expect(server.sent("POST", "/api/v1/auth/login")).toEqual([]);
    expect(server.sent("GET", "/api/v1/auth/csrf")).toEqual([]);
  });

  it("will not send a password without a name", async () => {
    const server = serveSignedOut();
    renderLogin();
    fireEvent.change(await screen.findByTestId("login-password"), {
      target: { value: "correct horse" },
    });

    fireEvent.click(screen.getByTestId("login-submit"));

    expect(server.sent("POST", "/api/v1/auth/login")).toEqual([]);
  });
});

describe("the sign-in page for someone who is already signed in", () => {
  it("takes them to the Dashboard instead of showing the form", async () => {
    serveRecordedReplies({
      [ME]: me,
      [BOOTSTRAP_STATUS]: bootstrapStatusAfterSetup,
    });
    const client = new QueryClient({
      defaultOptions: {
        queries: { retry: false },
        mutations: { retry: false },
      },
    });

    render(
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={["/login"]}>
          <Routes>
            <Route path="/login" element={<LoginPage />} />
            <Route path="/" element={<p>The Dashboard</p>} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    );

    expect(await screen.findByText("The Dashboard")).toBeInTheDocument();
    expect(screen.queryByTestId("login-form")).not.toBeInTheDocument();
  });
});
