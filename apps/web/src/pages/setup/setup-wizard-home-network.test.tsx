import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { authKeys } from "../../lib/auth/query-keys";
import { settingsKeys } from "../../lib/settings/query-keys";
import {
  serveRecordedReplies,
  type RecordedRoutes,
  type RecordedWeir,
} from "../../test/recorded-weir";
import {
  csrfToken,
  homeNetworkOff,
  homeNetworkOn,
  homeNetworkWrongPassword,
} from "../system/tabs/security/security-replies";
import { SetupWizardPage } from "./setup-wizard-page";

const { saveSettingsMock } = vi.hoisted(() => ({ saveSettingsMock: vi.fn() }));

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useNavigate: () => vi.fn(),
}));

vi.mock("../../lib/settings/queries", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../lib/settings/queries")>()),
  useAppSettingsSaveMutation: () => ({
    isPending: false,
    mutateAsync: saveSettingsMock,
  }),
}));

vi.mock("../../lib/processing/libraries-queries", () => ({
  useProcessingLibrariesQuery: () => ({ isPending: false, data: [] }),
  useCreateProcessingLibrary: () => ({
    isPending: false,
    mutateAsync: vi.fn(),
  }),
  useUpdateProcessingLibrary: () => ({
    isPending: false,
    mutateAsync: vi.fn(),
  }),
}));

const PATH = "/api/v1/suite/home-network-sign-in";
const QUESTION = "Skip sign-in for devices on your home network?";
const CSRF = "csrf.anonymous.atBNYQ.TcHTRYKKC8sTV8BMqob2iGMgIXY";

let weir: RecordedWeir | undefined;

function renderWizard(
  wizardState: string,
  saved: { enabled: boolean },
  routes: RecordedRoutes = {},
) {
  weir = serveRecordedReplies({
    "GET /api/v1/auth/csrf": csrfToken,
    ...routes,
  });
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: Infinity } },
  });
  client.setQueryData(authKeys.me, {
    id: 1,
    username: "someone",
    role: "admin",
  });
  client.setQueryData(settingsKeys.updateStatus, { install_type: "docker" });
  client.setQueryData(settingsKeys.homeNetworkSignIn, {
    ...saved,
    extra_networks: [],
    allow_list: [],
    warning: null,
  });
  client.setQueryData(settingsKeys.app, {
    setup_wizard_state: wizardState,
    app_timezone: "UTC",
    log_retention_days: 30,
    configuration_backup_enabled: false,
    configuration_backup_interval_hours: 24,
    configuration_backup_preferred_time: "02:00",
    configuration_backup_last_run_at: null,
    updated_at: "2026-04-23T00:00:00Z",
  });
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <SetupWizardPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const answer = (name: "Yes" | "No") =>
  screen.getByRole("radio", { name: `${QUESTION} ${name}` });
const finish = () => {
  fireEvent.click(
    screen.getByRole("radio", { name: "Neither – I'll pick folders myself" }),
  );
  fireEvent.click(screen.getByRole("button", { name: "Finish setup" }));
};

beforeEach(() => {
  saveSettingsMock.mockReset();
  saveSettingsMock.mockResolvedValue({});
});

afterEach(() => {
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  vi.unstubAllGlobals();
});

describe("Setup: skip sign-in for devices on your home network", () => {
  it("asks the question with Yes and No, and starts unanswered", () => {
    renderWizard("pending", { enabled: false });

    expect(screen.getByText(QUESTION)).toBeInTheDocument();
    expect(answer("Yes")).not.toBeChecked();
    expect(answer("No")).not.toBeChecked();
    expect(screen.queryByLabelText("Your password")).toBeNull();
  });

  it("finishes with the question unanswered, and leaves the setting off", async () => {
    renderWizard("pending", { enabled: false });

    finish();

    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(1));
    expect(weir!.sent("PUT", PATH)).toHaveLength(0);
  });

  it("finishes on No without asking for the password, since nothing changes", async () => {
    renderWizard("pending", { enabled: false });
    fireEvent.click(answer("No"));

    expect(screen.queryByLabelText("Your password")).toBeNull();
    finish();

    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(1));
    expect(weir!.sent("PUT", PATH)).toHaveLength(0);
  });

  it("will not finish on Yes until the password is typed, then saves the answer with it", async () => {
    renderWizard(
      "pending",
      { enabled: false },
      { [`PUT ${PATH}`]: homeNetworkOn },
    );
    fireEvent.click(answer("Yes"));

    finish();
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Type your password to save your answer about skipping sign-in",
    );
    expect(saveSettingsMock).not.toHaveBeenCalled();

    fireEvent.change(
      screen.getByLabelText("Your password", { selector: "input" }),
      {
        target: { value: "the owner's password" },
      },
    );
    fireEvent.click(screen.getByRole("button", { name: "Finish setup" }));

    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(1));
    expect(
      weir!.sent("PUT", PATH).map((request) => request.body),
    ).toStrictEqual([
      {
        enabled: true,
        extra_networks: [],
        allow_list: [],
        current_password: "the owner's password",
        csrf_token: CSRF,
      },
    ]);
  });

  it("does not finish setup when the server refuses the password", async () => {
    renderWizard(
      "pending",
      { enabled: false },
      { [`PUT ${PATH}`]: homeNetworkWrongPassword },
    );
    fireEvent.click(answer("Yes"));
    fireEvent.change(
      screen.getByLabelText("Your password", { selector: "input" }),
      {
        target: { value: "not it" },
      },
    );

    finish();

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Current password is not correct.",
    );
    expect(saveSettingsMock).not.toHaveBeenCalled();
  });

  it("starts from what is saved when setup is opened again, and asks nothing if the answer stands", async () => {
    renderWizard("completed", { enabled: true });

    expect(answer("Yes")).toBeChecked();
    fireEvent.click(
      screen.getByRole("radio", { name: "Neither – I'll pick folders myself" }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Save changes" }));

    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(1));
    expect(weir!.sent("PUT", PATH)).toHaveLength(0);
  });

  it("asks for the password when setup is opened again and the answer is changed to No", () => {
    renderWizard(
      "completed",
      { enabled: true },
      { [`PUT ${PATH}`]: homeNetworkOff },
    );

    fireEvent.click(answer("No"));

    expect(
      screen.getByLabelText("Your password", { selector: "input" }),
    ).toBeInTheDocument();
  });
});
