import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { clearSignedIn } from "../../lib/auth/signed-in-before";
import { LoginPage } from "./login-page";

/**
 * The sign-in page against a stubbed `fetch`, so the request a person's click sends and the sentence
 * each refusal becomes are judged together. The answers are the server's own, as the API tests of
 * AuthApiTests judge them.
 */

const json = (body: unknown, status = 200, headers: HeadersInit = {}) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json", ...headers },
  });

const admin = { id: 1, username: "alice", role: "admin", app_theme: "dark" };

type LoginRequest = Record<string, unknown>;

/** A Weir with an account already made, whose sign-in answers as `login` says. */
function serverSigningIn(login: () => Response | Error | Promise<Response>) {
  const attempts: LoginRequest[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      if (url === "/api/v1/auth/me")
        return json({ detail: "Not authenticated." }, 401);
      if (url === "/api/v1/auth/bootstrap/status") {
        return json({
          bootstrap_allowed: false,
          reason: "admin_already_exists",
          requires_setup_code: false,
        });
      }
      if (url === "/api/v1/auth/csrf") return json({ csrf_token: "token-1" });
      if (url === "/api/v1/auth/login") {
        expect(init?.method).toBe("POST");
        attempts.push(JSON.parse(String(init?.body)) as LoginRequest);
        const answer = login();
        if (answer instanceof Error) throw answer;
        return answer;
      }
      throw new Error(`unexpected request ${init?.method ?? "GET"} ${url}`);
    }),
  );
  return attempts;
}

function renderLogin(entry = "/login") {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[entry]}>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route path="/" element={<p>Signed in home</p>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

async function signInWith(username: string, password: string) {
  fireEvent.change(await screen.findByTestId("login-username"), {
    target: { value: username },
  });
  fireEvent.change(screen.getByTestId("login-password"), {
    target: { value: password },
  });
  fireEvent.click(screen.getByTestId("login-submit"));
}

beforeEach(() => {
  clearSignedIn();
  sessionStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
  clearSignedIn();
  sessionStorage.clear();
});

describe("the sign-in request", () => {
  it("sends the name without stray spaces, the password, the token and a device that is not trusted", async () => {
    const attempts = serverSigningIn(() => json({ user: admin }));
    renderLogin();

    await signInWith("  alice ", "correct horse");

    expect(await screen.findByText("Signed in home")).toBeInTheDocument();
    expect(attempts).toEqual([
      {
        username: "alice",
        password: "correct horse",
        csrf_token: "token-1",
        trusted_device: false,
      },
    ]);
  });

  it("sends trusted_device as true when Trust this device is ticked", async () => {
    const attempts = serverSigningIn(() => json({ user: admin }));
    renderLogin();
    fireEvent.click(await screen.findByLabelText("Trust this device"));

    await signInWith("alice", "correct horse");

    expect(await screen.findByText("Signed in home")).toBeInTheDocument();
    expect(attempts).toEqual([
      {
        username: "alice",
        password: "correct horse",
        csrf_token: "token-1",
        trusted_device: true,
      },
    ]);
  });
});

describe("the sign-in form", () => {
  it("signs in when the form itself is submitted, as Enter in a field does, and keeps the browser from reloading the page", async () => {
    const attempts = serverSigningIn(() => json({ user: admin }));
    renderLogin();
    fireEvent.change(await screen.findByTestId("login-username"), {
      target: { value: "alice" },
    });
    fireEvent.change(screen.getByTestId("login-password"), {
      target: { value: "correct horse" },
    });

    const notPrevented = fireEvent.submit(screen.getByTestId("login-form"));

    expect(notPrevented).toBe(false);
    expect(await screen.findByText("Signed in home")).toBeInTheDocument();
    expect(attempts).toHaveLength(1);
    expect(attempts[0]).toMatchObject({
      username: "alice",
      password: "correct horse",
    });
  });

  it("holds the Sign in button as Signing in… while the answer is awaited, so a second click sends nothing", async () => {
    let answer: (reply: Response) => void = () => undefined;
    const attempts = serverSigningIn(
      () =>
        new Promise<Response>((resolve) => {
          answer = resolve;
        }),
    );
    renderLogin();

    await signInWith("alice", "correct horse");

    await waitFor(() => expect(attempts).toHaveLength(1));
    const submit = screen.getByTestId("login-submit");
    await waitFor(() => expect(submit).toBeDisabled());
    expect(submit).toHaveTextContent("Signing in…");
    fireEvent.click(submit);
    expect(attempts).toHaveLength(1);

    answer(json({ user: admin }));
    expect(await screen.findByText("Signed in home")).toBeInTheDocument();
    expect(attempts).toHaveLength(1);
  });
});

describe("a sign-in that is refused", () => {
  it("shows the server's reason for a wrong name or password, keeps what was typed and lets the person try again", async () => {
    serverSigningIn(() =>
      json({ detail: "Invalid username or password." }, 401),
    );
    renderLogin();

    await signInWith("alice", "wrong horse");

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Invalid username or password.",
    );
    expect(screen.getByTestId("login-username")).toHaveValue("alice");
    expect(screen.getByTestId("login-password")).toHaveValue("wrong horse");
    expect(screen.getByTestId("login-submit")).toBeEnabled();
    expect(screen.getByTestId("login-submit")).toHaveTextContent("Sign in");
    expect(screen.queryByText("Signed in home")).not.toBeInTheDocument();
  });

  it("tells a person who tried too often to wait and try again, whether the address or the account was limited", async () => {
    const answers = [
      () =>
        json(
          {
            detail:
              "Too many login attempts from this address. Try again later.",
          },
          429,
          { "Retry-After": "60" },
        ),
      () =>
        json(
          {
            detail:
              "Too many sign-in attempts for this account. Wait a few minutes, then try again.",
          },
          429,
          { "Retry-After": "300" },
        ),
    ];
    let asked = 0;
    serverSigningIn(() => answers[asked++]());
    renderLogin();

    await signInWith("alice", "wrong horse");
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Too many attempts. Wait a few minutes, then try again.",
    );

    fireEvent.click(screen.getByTestId("login-submit"));
    await waitFor(() => expect(asked).toBe(2));
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Too many attempts. Wait a few minutes, then try again.",
    );
    expect(screen.getByTestId("login-submit")).toBeEnabled();
  });

  it("says Weir can't be reached, not the browser's own words, when the server does not answer", async () => {
    serverSigningIn(() => new TypeError("Failed to fetch"));
    renderLogin();

    await signInWith("alice", "correct horse");

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent(
      "Can't reach Weir. Check it's still running.",
    );
    expect(alert).not.toHaveTextContent("Failed to fetch");
    expect(screen.getByTestId("login-username")).toHaveValue("alice");
    expect(screen.getByTestId("login-submit")).toBeEnabled();
  });
});

describe("a sign-in the browser dropped", () => {
  it("explains that the password was right but the session did not stick, and what to set", async () => {
    serverSigningIn(() => json({ user: admin }));
    renderLogin("/login?problem=session-not-kept");

    const alert = await screen.findByRole("alert");

    expect(alert).toHaveTextContent(
      "Your password was correct, but the session did not stick.",
    );
    expect(alert).toHaveTextContent(
      "Your browser rejected the sign-in cookie.",
    );
    expect(alert).toHaveTextContent("WEIR_SESSION_COOKIE_SECURE=auto");
    expect(screen.getByTestId("login-form")).toBeInTheDocument();
  });

  it("says nothing of it on an ordinary visit", async () => {
    serverSigningIn(() => json({ user: admin }));
    renderLogin();

    await screen.findByTestId("login-form");

    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });
});
