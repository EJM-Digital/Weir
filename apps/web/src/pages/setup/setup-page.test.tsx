import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

import {
  NETWORK_UNREACHABLE_MESSAGE,
  ApiHttpError,
} from "../../lib/api/client";
import { SetupPage } from "./setup-page";

const navigateMock = vi.fn();
const mutateAsyncMock = vi.fn();
const resetMock = vi.fn();
let bootstrapMutationError: unknown = null;
let requiresSetupCode = false;

vi.mock("react-router-dom", async (importOriginal) => {
  const actual = await importOriginal<typeof import("react-router-dom")>();
  return {
    ...actual,
    useNavigate: () => navigateMock,
  };
});

vi.mock("../../lib/auth/queries", () => ({
  useMeQuery: () => ({ isPending: false, data: null }),
  useBootstrapStatusQuery: () => ({
    isPending: false,
    isError: false,
    data: {
      bootstrap_allowed: true,
      reason: "no_admin_user",
      requires_setup_code: requiresSetupCode,
    },
  }),
  useBootstrapMutation: () => ({
    isPending: false,
    isError: bootstrapMutationError !== null,
    error: bootstrapMutationError,
    mutateAsync: mutateAsyncMock,
    reset: resetMock,
  }),
}));

function wrap(ui: ReactNode) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return (
    <QueryClientProvider client={client}>
      <MemoryRouter>{ui}</MemoryRouter>
    </QueryClientProvider>
  );
}

describe("SetupPage", () => {
  beforeEach(() => {
    navigateMock.mockReset();
    mutateAsyncMock.mockReset();
    resetMock.mockReset();
    bootstrapMutationError = null;
    requiresSetupCode = false;
  });

  it("blocks bootstrap submit when password is shorter than 8 characters", async () => {
    render(wrap(<SetupPage />));

    fireEvent.change(screen.getByTestId("setup-username"), {
      target: { value: "admin" },
    });
    fireEvent.change(screen.getByTestId("setup-password"), {
      target: { value: "short" },
    });
    fireEvent.change(screen.getByTestId("setup-confirm-password"), {
      target: { value: "short" },
    });
    fireEvent.submit(screen.getByTestId("setup-form"));

    expect(mutateAsyncMock).not.toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent(
      "Password must be at least 8 characters.",
    );
  });

  it("blocks bootstrap submit when the confirmation does not match", async () => {
    render(wrap(<SetupPage />));

    fireEvent.change(screen.getByTestId("setup-username"), {
      target: { value: "admin" },
    });
    fireEvent.change(screen.getByTestId("setup-password"), {
      target: { value: "password-strong" },
    });
    fireEvent.change(screen.getByTestId("setup-confirm-password"), {
      target: { value: "password-different" },
    });
    fireEvent.submit(screen.getByTestId("setup-form"));

    expect(mutateAsyncMock).not.toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent(
      "Passwords do not match.",
    );
  });

  it("submits bootstrap when username and password meet the requirements, then signs straight in", async () => {
    mutateAsyncMock.mockResolvedValue({
      message: "ok",
      username: "admin",
      user: { id: 1, username: "admin", role: "admin" },
    });

    render(wrap(<SetupPage />));

    fireEvent.change(screen.getByTestId("setup-username"), {
      target: { value: " admin " },
    });
    fireEvent.change(screen.getByTestId("setup-password"), {
      target: { value: "password-strong" },
    });
    fireEvent.change(screen.getByTestId("setup-confirm-password"), {
      target: { value: "password-strong" },
    });
    fireEvent.submit(screen.getByTestId("setup-form"));

    expect(mutateAsyncMock).toHaveBeenCalledWith({
      username: "admin",
      password: "password-strong",
    });
    await waitFor(() => {
      expect(navigateMock).toHaveBeenCalledWith("/", { replace: true });
    });
  });

  it("hides the setup code field when the peer does not need one", () => {
    render(wrap(<SetupPage />));

    expect(screen.queryByTestId("setup-code")).not.toBeInTheDocument();
  });

  it("requires and submits the setup code when the peer needs one", async () => {
    requiresSetupCode = true;
    mutateAsyncMock.mockResolvedValue({
      message: "ok",
      username: "admin",
      user: { id: 1, username: "admin", role: "admin" },
    });

    render(wrap(<SetupPage />));

    fireEvent.change(screen.getByTestId("setup-username"), {
      target: { value: "admin" },
    });
    fireEvent.change(screen.getByTestId("setup-password"), {
      target: { value: "password-strong" },
    });
    fireEvent.change(screen.getByTestId("setup-confirm-password"), {
      target: { value: "password-strong" },
    });
    fireEvent.change(screen.getByTestId("setup-code"), {
      target: { value: "ABCD-1234" },
    });
    fireEvent.submit(screen.getByTestId("setup-form"));

    expect(mutateAsyncMock).toHaveBeenCalledWith({
      username: "admin",
      password: "password-strong",
      setupCode: "ABCD-1234",
    });
    await waitFor(() => {
      expect(navigateMock).toHaveBeenCalledWith("/", { replace: true });
    });
  });

  it("shows the friendly can't-reach-it message, never the raw browser text, when the server is unreachable", () => {
    bootstrapMutationError = new ApiHttpError(
      "/api/v1/auth/bootstrap",
      0,
      NETWORK_UNREACHABLE_MESSAGE,
      undefined,
      false,
      true,
    );

    render(wrap(<SetupPage />));

    const banner = screen.getByRole("alert");
    expect(banner).toHaveTextContent(NETWORK_UNREACHABLE_MESSAGE);
    expect(banner).not.toHaveTextContent("Failed to fetch");
  });

  it("forgets the server's reason once a field is edited, and only then", () => {
    bootstrapMutationError = new ApiHttpError(
      "/api/v1/auth/bootstrap",
      409,
      "Username already exists.",
      undefined,
      false,
      false,
    );

    render(wrap(<SetupPage />));
    expect(resetMock).not.toHaveBeenCalled();
    fireEvent.change(screen.getByTestId("setup-username"), {
      target: { value: "someone-else" },
    });

    expect(resetMock).toHaveBeenCalledTimes(1);
  });
});
