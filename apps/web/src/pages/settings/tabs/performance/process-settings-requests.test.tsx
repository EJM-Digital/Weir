import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import {
  serveRecordedReplies,
  type RecordedRoutes,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import {
  costsSaved,
  csrfToken,
  filesAtOnce,
  saveRefusedTokenExpired,
  settingsAfterCosts,
  settingsBudgetOn,
  signedInAdmin,
} from "../operator-settings-replies";
import { ProcessSettingsSection } from "./process-settings-section";

const OPERATOR_SETTINGS = "/api/v1/processing/operator-settings";
const SAVE = "Save performance settings";

const token = (JSON.parse(csrfToken.body) as { csrf_token: string }).csrf_token;

let weir: RecordedWeir | undefined;

afterEach(() => {
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  vi.unstubAllGlobals();
});

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

async function renderSection(
  routes: RecordedRoutes = {},
): Promise<RecordedWeir> {
  weir = serveRecordedReplies({
    "GET /api/v1/auth/csrf": csrfToken,
    "GET /api/v1/auth/me": signedInAdmin,
    "GET /api/v1/processing/files-at-once": filesAtOnce,
    "GET /api/v1/processing/operator-settings": settingsBudgetOn,
    ...routes,
  });
  render(<ProcessSettingsSection />, { wrapper });
  await screen.findByRole("button", { name: "2", pressed: true });
  return weir;
}

function type(label: string, value: string): void {
  fireEvent.change(screen.getByLabelText(label), { target: { value } });
}

describe("the Budget and the cost of each resolution", () => {
  it("lands each number in its own field of the saved settings", async () => {
    const server = await renderSection({
      [`PUT ${OPERATOR_SETTINGS}`]: costsSaved,
      [`GET ${OPERATOR_SETTINGS}`]: [settingsBudgetOn, settingsAfterCosts],
    });

    type("Budget", "12");
    type("An SD file costs", "2");
    type("A 720p file costs", "3");
    type("A 1080p file costs", "5");
    type("A 4K file costs", "8");
    fireEvent.click(screen.getByRole("button", { name: SAVE }));

    expect(
      await screen.findByRole("button", { name: "2", pressed: true }),
    ).toBeInTheDocument();
    expect(server.sent("PUT", OPERATOR_SETTINGS).map((r) => r.body)).toEqual([
      {
        max_concurrent_files: 2,
        runner_capacity: 12,
        runner_cost_sd: 2,
        runner_cost_720p: 3,
        runner_cost_1080p: 5,
        runner_cost_4k: 8,
        runner_budget_enabled: true,
        csrf_token: token,
      },
    ]);
    expect(await screen.findByLabelText("Budget")).toHaveValue(12);
    expect(screen.getByLabelText("An SD file costs")).toHaveValue(2);
    expect(screen.getByLabelText("A 720p file costs")).toHaveValue(3);
    expect(screen.getByLabelText("A 1080p file costs")).toHaveValue(5);
    expect(screen.getByLabelText("A 4K file costs")).toHaveValue(8);
    expect(
      screen.queryByRole("button", { name: SAVE }),
    ).not.toBeInTheDocument();
  });

  describe("a number outside its range, or left blank", () => {
    it.each([
      ["Budget", "0"],
      ["Budget", "65"],
      ["Budget", ""],
      ["An SD file costs", "65"],
      ["An SD file costs", ""],
      ["An SD file costs", "-1"],
      ["A 720p file costs", "65"],
      ["A 720p file costs", ""],
      ["A 1080p file costs", "65"],
      ["A 1080p file costs", ""],
      ["A 4K file costs", "65"],
      ["A 4K file costs", ""],
    ])("cannot be saved: %s as %j", async (label, value) => {
      const server = await renderSection();

      type(label, value);

      const save = screen.getByRole("button", { name: SAVE });
      expect(save).toBeDisabled();
      fireEvent.click(save);
      expect(server.sent("PUT", OPERATOR_SETTINGS)).toEqual([]);
    });
  });

  describe("a number at the edge of its range", () => {
    it.each([
      ["Budget", "1"],
      ["Budget", "64"],
      ["An SD file costs", "0"],
      ["An SD file costs", "64"],
      ["A 720p file costs", "0"],
      ["A 1080p file costs", "64"],
      ["A 4K file costs", "0"],
    ])("can be saved: %s as %j", async (label, value) => {
      await renderSection();

      type(label, value);

      expect(screen.getByRole("button", { name: SAVE })).toBeEnabled();
    });
  });

  it("offers a way back to a saving state after an out-of-range number is corrected", async () => {
    await renderSection();

    type("Budget", "65");
    expect(screen.getByRole("button", { name: SAVE })).toBeDisabled();
    type("Budget", "7");

    expect(screen.getByRole("button", { name: SAVE })).toBeEnabled();
  });
});

describe("a save that fails", () => {
  it("shows the server's reason, keeps what was typed, and leaves Save available", async () => {
    const server = await renderSection({
      [`PUT ${OPERATOR_SETTINGS}`]: saveRefusedTokenExpired,
    });

    type("Budget", "12");
    fireEvent.click(screen.getByRole("button", { name: SAVE }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Your confirmation token expired. Refresh the page and try again.",
    );
    expect(server.sent("PUT", OPERATOR_SETTINGS)).toHaveLength(1);
    expect(screen.getByLabelText("Budget")).toHaveValue(12);
    expect(screen.getByRole("button", { name: SAVE })).toBeEnabled();
  });

  it("says Weir cannot be reached when the request never arrives", async () => {
    await renderSection({
      [`PUT ${OPERATOR_SETTINGS}`]: () =>
        Promise.reject(new TypeError("Failed to fetch")),
    });

    type("Budget", "12");
    fireEvent.click(screen.getByRole("button", { name: SAVE }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Can't reach Weir. Check it's still running.",
    );
    expect(screen.getByRole("button", { name: SAVE })).toBeEnabled();
  });

  it("can be tried again after it fails", async () => {
    const server = await renderSection({
      [`PUT ${OPERATOR_SETTINGS}`]: [saveRefusedTokenExpired, costsSaved],
      [`GET ${OPERATOR_SETTINGS}`]: [settingsBudgetOn, settingsAfterCosts],
    });

    type("Budget", "12");
    type("An SD file costs", "2");
    type("A 720p file costs", "3");
    type("A 1080p file costs", "5");
    type("A 4K file costs", "8");
    fireEvent.click(screen.getByRole("button", { name: SAVE }));
    await screen.findByRole("alert");
    fireEvent.click(screen.getByRole("button", { name: SAVE }));

    expect(await screen.findByLabelText("Budget")).toHaveValue(12);
    expect(server.sent("PUT", OPERATOR_SETTINGS)).toHaveLength(2);
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });
});
