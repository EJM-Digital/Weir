import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  within,
} from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import {
  serveRecordedReplies,
  type RecordedReply,
  type RecordedWeir,
} from "../../test/recorded-weir";
import {
  codeAccepted,
  codeRefused,
  codeTriesLimited,
  csrfToken,
  passwordSet,
  passwordTooCommon,
  resetTimedOut,
} from "../../test/recovery-replies";
import { RecoverPage } from "./recover-page";

const CSRF = "GET /api/v1/auth/csrf";
const CODE = "POST /api/v1/auth/recovery/code";
const PASSWORD = "POST /api/v1/auth/recovery/password";
const TOKEN = "1:47130fc21f8d0ead.auMObw.SyXAuDukVuAxW9ryLn-uFbBwBTY";
const CSRF_TOKEN = "csrf.anonymous.auMOaA.2XNpaZ88IDzKnV9V3SO4ykv4tlo";

let weir: RecordedWeir | undefined;

afterEach(() => {
  cleanup();
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  vi.unstubAllGlobals();
});

function SignIn() {
  const location = useLocation();
  return <p data-testid="sign-in">{location.pathname + location.search}</p>;
}

function renderRecover(
  code: RecordedReply | RecordedReply[] | (() => Promise<RecordedReply>),
  password:
    | RecordedReply
    | RecordedReply[]
    | (() => Promise<RecordedReply>) = passwordSet,
) {
  weir = serveRecordedReplies({
    [CSRF]: csrfToken,
    [CODE]: code,
    [PASSWORD]: password,
  });
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={["/recover"]}>
        <Routes>
          <Route path="/recover" element={<RecoverPage />} />
          <Route path="/login" element={<SignIn />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

async function spendCode(code = "k7m2p-9xq4t-hwb3d-rn6yc") {
  fireEvent.change(screen.getByTestId("recover-code"), {
    target: { value: `  ${code}  ` },
  });
  fireEvent.click(screen.getByTestId("recover-code-submit"));
}

function typePasswords(first: string, second: string) {
  fireEvent.change(screen.getByTestId("recover-password"), {
    target: { value: first },
  });
  fireEvent.change(screen.getByTestId("recover-confirm-password"), {
    target: { value: second },
  });
  fireEvent.click(screen.getByTestId("recover-password-submit"));
}

describe("getting back in with a recovery code", () => {
  it("asks for a code and says it is used up as soon as it is accepted", () => {
    renderRecover(codeAccepted);

    expect(screen.getByRole("heading", { name: "Get back in" })).toBeVisible();
    expect(screen.getByLabelText("Recovery code")).toHaveFocus();
    expect(
      screen.getByText(/used up as soon as Weir accepts it, even if you stop/),
    ).toBeVisible();
    expect(
      screen.getByRole("link", { name: "Back to sign in" }),
    ).toHaveAttribute("href", "/login");
  });

  it("sends the code as typed, trimmed, with a fresh token, and shows the username it belongs to", async () => {
    renderRecover(codeAccepted);

    await spendCode();

    expect(await screen.findByTestId("recover-username")).toHaveTextContent(
      "recorded-admin",
    );
    expect(weir?.sent("POST", "/api/v1/auth/recovery/code")).toEqual([
      expect.objectContaining({
        body: { csrf_token: CSRF_TOKEN, code: "k7m2p-9xq4t-hwb3d-rn6yc" },
      }),
    ]);
    expect(
      screen.getByText(/Everyone who was signed in has been signed out/),
    ).toBeVisible();
    expect(screen.queryByTestId("recover-code")).toBeNull();
  });

  it("sends nothing for an empty code and says so", () => {
    renderRecover(codeAccepted);

    fireEvent.click(screen.getByTestId("recover-code-submit"));

    expect(screen.getByRole("alert")).toHaveTextContent(
      "Enter a recovery code.",
    );
    expect(weir?.sent("POST", "/api/v1/auth/recovery/code")).toEqual([]);
  });

  it("forgets the empty-code reason once a code is sent", async () => {
    renderRecover(codeAccepted);
    fireEvent.click(screen.getByTestId("recover-code-submit"));
    expect(screen.getByRole("alert")).toBeVisible();

    await spendCode();

    await screen.findByTestId("recover-username");
  });

  it("shows Weir's own reason when the code is refused, and takes another", async () => {
    renderRecover([codeRefused, codeAccepted]);

    await spendCode("AAAAA-AAAAA-AAAAA-AAAAA");

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "That recovery code isn't right, or it has been used already. Check it and try again.",
    );
    expect(screen.getByTestId("recover-code-submit")).toBeEnabled();
    fireEvent.click(screen.getByTestId("recover-code-submit"));
    expect(await screen.findByTestId("recover-username")).toBeVisible();
  });

  it("says when too many tries have been made", async () => {
    renderRecover(codeTriesLimited);

    await spendCode();

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Too many attempts. Wait a few minutes, then try again.",
    );
  });

  it("says it is checking while Weir answers and cannot be pressed twice", async () => {
    let answer: (reply: RecordedReply) => void = () => {};
    renderRecover(
      () => new Promise<RecordedReply>((resolve) => (answer = resolve)),
    );

    await spendCode();

    const busy = await screen.findByRole("button", { name: "Checking…" });
    expect(busy).toBeDisabled();
    answer(codeAccepted);
    expect(await screen.findByTestId("recover-username")).toBeVisible();
    expect(weir?.sent("POST", "/api/v1/auth/recovery/code")).toHaveLength(1);
  });
});

describe("choosing the new password after a code", () => {
  async function toPasswordStep(
    password?: RecordedReply | RecordedReply[] | (() => Promise<RecordedReply>),
  ) {
    renderRecover(codeAccepted, password);
    await spendCode();
    await screen.findByTestId("recover-username");
  }

  it("sets the password with the token the code gave, then opens sign-in with the password-changed notice", async () => {
    await toPasswordStep();

    typePasswords("a-brand-new-password", "a-brand-new-password");

    expect(await screen.findByTestId("sign-in")).toHaveTextContent(
      "/login?password=changed",
    );
    expect(weir?.sent("POST", "/api/v1/auth/recovery/password")).toEqual([
      expect.objectContaining({
        body: {
          csrf_token: CSRF_TOKEN,
          recovery_token: TOKEN,
          new_password: "a-brand-new-password",
        },
      }),
    ]);
  });

  it("sends nothing when the password is shorter than eight characters", async () => {
    await toPasswordStep();

    typePasswords("short", "short");

    expect(screen.getByRole("alert")).toHaveTextContent(
      "Password must be at least 8 characters.",
    );
    expect(weir?.sent("POST", "/api/v1/auth/recovery/password")).toEqual([]);
  });

  it("sends nothing when the two passwords differ", async () => {
    await toPasswordStep();

    typePasswords("a-brand-new-password", "a-brand-new-passwor");

    expect(screen.getByRole("alert")).toHaveTextContent(
      "Passwords do not match.",
    );
    expect(weir?.sent("POST", "/api/v1/auth/recovery/password")).toEqual([]);
  });

  it("shows Weir's own reason for a password it refuses, keeps the form, and takes a better one", async () => {
    await toPasswordStep([passwordTooCommon, passwordSet]);

    typePasswords("password1234", "password1234");

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Password is too common. Choose a stronger password.",
    );
    expect(screen.getByTestId("recover-password-submit")).toBeEnabled();
    typePasswords("a-brand-new-password", "a-brand-new-password");
    expect(await screen.findByTestId("sign-in")).toBeVisible();
  });

  it("clears an earlier reason of its own when the next try is sent", async () => {
    await toPasswordStep();
    typePasswords("short", "short");
    expect(screen.getByRole("alert")).toBeVisible();

    typePasswords("a-brand-new-password", "a-brand-new-password");

    expect(await screen.findByTestId("sign-in")).toBeVisible();
  });

  it("says it is saving while Weir answers and cannot be pressed twice", async () => {
    let answer: (reply: RecordedReply) => void = () => {};
    await toPasswordStep(
      () => new Promise<RecordedReply>((resolve) => (answer = resolve)),
    );

    typePasswords("a-brand-new-password", "a-brand-new-password");

    const busy = await screen.findByRole("button", { name: "Saving…" });
    expect(busy).toBeDisabled();
    answer(passwordSet);
    await screen.findByTestId("sign-in");
    expect(weir?.sent("POST", "/api/v1/auth/recovery/password")).toHaveLength(
      1,
    );
  });

  it("goes back to asking for a code when the person wants to use another", async () => {
    await toPasswordStep(resetTimedOut);
    typePasswords("a-brand-new-password", "a-brand-new-password");
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "This password reset has timed out. Use another recovery code to start again.",
    );

    fireEvent.click(screen.getByTestId("recover-start-again"));

    const form = screen.getByTestId("recover-code-form");
    expect(within(form).getByTestId("recover-code")).toHaveValue("");
    expect(screen.queryByRole("alert")).toBeNull();
  });
});
