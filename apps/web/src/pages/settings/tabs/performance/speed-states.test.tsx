import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { processingKeys } from "../../../../lib/processing/query-keys";
import { heldReply } from "../../../../test/held-reply";
import { adminMe, csrfToken } from "../../../../test/recorded-admin-session";
import {
  serveRecordedReplies,
  type RecordedReply,
  type RecordedRoutes,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import {
  filesAtOnceNothingWaiting,
  operatorSettings,
  operatorSettingsSavedAsFour,
} from "./performance-replies";
import { SpeedTab } from "./speed-tab";

const SETTINGS_PATH = "/api/v1/processing/operator-settings";
const SAVE = "Save performance settings";
const BUDGET_SWITCH = "Also weigh files by resolution";

const roleMe = (role: string): RecordedReply => ({
  status: 200,
  body: adminMe.body.replace('"role":"admin"', `"role":"${role}"`),
});

let weir: RecordedWeir | undefined;
let client: QueryClient;

afterEach(() => {
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  vi.unstubAllGlobals();
});

function renderSpeed(routes: RecordedRoutes = {}): RecordedWeir {
  weir = serveRecordedReplies({
    "GET /api/v1/auth/csrf": csrfToken,
    "GET /api/v1/auth/me": adminMe,
    [`GET ${SETTINGS_PATH}`]: operatorSettings,
    "GET /api/v1/processing/files-at-once": filesAtOnceNothingWaiting,
    ...routes,
  });
  client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  render(
    <QueryClientProvider client={client}>
      <SpeedTab />
    </QueryClientProvider>,
  );
  return weir;
}

const choice = (n: number) => screen.getByRole("button", { name: String(n) });

describe("opening Speed", () => {
  it("says the performance settings are loading until they arrive", async () => {
    const answer = heldReply(operatorSettings);
    renderSpeed({ [`GET ${SETTINGS_PATH}`]: answer.route });

    expect(await screen.findByRole("status")).toHaveTextContent(
      "Loading performance settings",
    );
    expect(
      screen.queryByRole("group", { name: "Files at once" }),
    ).not.toBeInTheDocument();

    answer.release();
    expect(
      await screen.findByRole("button", { name: "1", pressed: true }),
    ).toBeInTheDocument();
  });
});

describe("saving Speed", () => {
  it("says Saving… and holds every control still while the server answers", async () => {
    const answer = heldReply(operatorSettingsSavedAsFour);
    const server = renderSpeed({ [`PUT ${SETTINGS_PATH}`]: answer.route });
    await screen.findByRole("button", { name: "1", pressed: true });
    fireEvent.click(choice(4));

    fireEvent.click(screen.getByRole("button", { name: SAVE }));

    const saving = await screen.findByRole("button", { name: "Saving…" });
    expect(saving).toBeDisabled();
    for (const n of [1, 2, 3, 4, 10]) expect(choice(n)).toBeDisabled();
    for (const side of ["On", "Off"]) {
      expect(
        within(
          screen.getByRole("radiogroup", { name: BUDGET_SWITCH }),
        ).getByRole("radio", { name: side }),
      ).toBeDisabled();
    }
    fireEvent.click(saving);
    expect(server.sent("PUT", SETTINGS_PATH)).toHaveLength(1);
    answer.release();
    await waitFor(() => expect(choice(4)).toBeEnabled());
    expect(choice(4)).toHaveAttribute("aria-pressed", "true");
    expect(
      screen.queryByRole("button", { name: SAVE }),
    ).not.toBeInTheDocument();
  });
});

describe("Speed changed from somewhere else while a number is half chosen", () => {
  it("shows the saved settings and drops the choice that was not saved", async () => {
    renderSpeed({
      [`GET ${SETTINGS_PATH}`]: [operatorSettings, operatorSettingsSavedAsFour],
    });
    await screen.findByRole("button", { name: "1", pressed: true });
    fireEvent.click(choice(7));
    expect(screen.getByRole("button", { name: SAVE })).toBeInTheDocument();

    await act(async () => {
      await client.invalidateQueries({
        queryKey: processingKeys.operatorSettings,
      });
    });

    expect(
      await screen.findByRole("button", { name: "4", pressed: true }),
    ).toBeInTheDocument();
    expect(choice(7)).toHaveAttribute("aria-pressed", "false");
    expect(
      screen.queryByRole("button", { name: SAVE }),
    ).not.toBeInTheDocument();
  });
});

describe("a viewer on Speed", () => {
  it("sees the settings with every control and Save held still", async () => {
    renderSpeed({ "GET /api/v1/auth/me": roleMe("viewer") });
    await screen.findByRole("button", { name: "1", pressed: true });

    for (const n of [1, 2, 3, 4, 10]) expect(choice(n)).toBeDisabled();
    for (const side of ["On", "Off"]) {
      expect(
        within(
          screen.getByRole("radiogroup", { name: BUDGET_SWITCH }),
        ).getByRole("radio", { name: side }),
      ).toBeDisabled();
    }
    fireEvent.click(choice(4));
    expect(choice(1)).toHaveAttribute("aria-pressed", "true");
    expect(
      screen.queryByRole("button", { name: SAVE }),
    ).not.toBeInTheDocument();
  });

  it("can change them as an operator", async () => {
    renderSpeed({ "GET /api/v1/auth/me": roleMe("operator") });
    await screen.findByRole("button", { name: "1", pressed: true });

    fireEvent.click(choice(4));

    expect(screen.getByRole("button", { name: SAVE })).toBeEnabled();
  });
});
