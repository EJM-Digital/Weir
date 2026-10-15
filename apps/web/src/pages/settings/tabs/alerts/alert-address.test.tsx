import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, within } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { adminMe, csrfToken } from "../../../../test/recorded-admin-session";
import {
  serveRecordedReplies,
  type RecordedRoutes,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import {
  addressInUse,
  addressReplaced,
  savedKeepingTheAddress,
  tickSaved,
  twoAlerts,
  twoAlertsAfterTick,
} from "./alert-address-replies";
import { AlertsTab } from "./alerts-tab";

const CHANNELS = "/api/v1/suite/notification-channels";

/** The part of a Discord-style address that lets anyone post as the alert, as it was typed when the alert was made. */
const SAVED_TOKEN = "s3cr3t-T0ken_abc";
const NEW_TOKEN = "replacement-T0ken_xyz";
const NEW_ADDRESS = `https://hooks.weir-recording.invalid/api/webhooks/1034598712/${NEW_TOKEN}`;
const SHOWN_ADDRESS =
  "https://hooks.weir-recording.invalid/api/webhooks/1034598712/••••";
const OTHER_ALERTS_ADDRESS =
  "https://hooks.weir-recording.invalid/ops/6d41b0c8-token";

const token = (JSON.parse(csrfToken.body) as { csrf_token: string }).csrf_token;

const CONSOLE_METHODS = [
  "debug",
  "error",
  "info",
  "log",
  "trace",
  "warn",
] as const;

let weir: RecordedWeir | undefined;
let consoleOutput: string[];

beforeEach(() => {
  consoleOutput = [];
  for (const method of CONSOLE_METHODS) {
    vi.spyOn(console, method).mockImplementation((...args: unknown[]) => {
      consoleOutput.push(args.map((arg) => String(arg)).join(" "));
    });
  }
  localStorage.clear();
  sessionStorage.clear();
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

function serve(routes: RecordedRoutes): RecordedWeir {
  weir = serveRecordedReplies({
    "GET /api/v1/auth/csrf": csrfToken,
    "GET /api/v1/auth/me": adminMe,
    [`GET ${CHANNELS}`]: twoAlerts,
    ...routes,
  });
  return weir;
}

/** Everything this browser tab keeps besides the page itself, which an address's token must not be found in. */
function everythingKept(): string {
  const stored = (storage: Storage) =>
    Array.from({ length: storage.length }, (_, i) => {
      const key = storage.key(i) ?? "";
      return `${key}=${storage.getItem(key)}`;
    });
  return [
    ...stored(localStorage),
    ...stored(sessionStorage),
    ...consoleOutput,
    document.cookie,
  ].join("\n");
}

async function rowOf(label: string): Promise<HTMLElement> {
  const row = (await screen.findByText(label)).closest("tr");
  if (!(row instanceof HTMLElement)) throw new Error(`no row for ${label}`);
  return row;
}

async function openEditForm(label: string): Promise<HTMLElement> {
  fireEvent.click(
    within(await rowOf(label)).getByRole("button", { name: "Edit" }),
  );
  return screen.findByRole("region", { name: "Edit alert" });
}

function save(form: HTMLElement): void {
  fireEvent.click(within(form).getByRole("button", { name: "Save alert" }));
}

describe("an alert's address in the list", () => {
  it("shows the address as the server gave it, with the token already hidden", async () => {
    serve({});
    render(<AlertsTab />, { wrapper });

    const row = await rowOf("Discord alerts");

    expect(within(row).getByText(SHOWN_ADDRESS)).toBeInTheDocument();
    expect(document.body.innerHTML).not.toContain(SAVED_TOKEN);
  });
});

describe("the Edit alert form", () => {
  it("shows the address with its token hidden and offers to replace it, without a field to type in", async () => {
    serve({});
    render(<AlertsTab />, { wrapper });

    const form = await openEditForm("Discord alerts");

    expect(within(form).getByText(SHOWN_ADDRESS)).toBeInTheDocument();
    expect(
      within(form).getByRole("button", { name: "Replace the address" }),
    ).toBeEnabled();
    expect(
      within(form).queryByLabelText("Webhook URL"),
    ).not.toBeInTheDocument();
    expect(document.body.innerHTML).not.toContain(SAVED_TOKEN);
  });

  it("saves without sending an address, so the saved one is kept", async () => {
    const server = serve({
      [`PUT ${CHANNELS}/1`]: savedKeepingTheAddress,
    });
    render(<AlertsTab />, { wrapper });
    const form = await openEditForm("Discord alerts");

    fireEvent.change(within(form).getByLabelText("Label"), {
      target: { value: "Discord alerts" },
    });
    save(form);

    await vi.waitFor(() =>
      expect(
        screen.queryByRole("region", { name: "Edit alert" }),
      ).not.toBeInTheDocument(),
    );
    expect(server.sent("PUT", `${CHANNELS}/1`).map((r) => r.body)).toEqual([
      {
        label: "Discord alerts",
        provider: "discord",
        events: ["job_failed", "processing_job_failed"],
        enabled: true,
        csrf_token: token,
      },
    ]);
  });

  it("asks for the new address once it is to be replaced, and will not save it blank", async () => {
    serve({});
    render(<AlertsTab />, { wrapper });
    const form = await openEditForm("Discord alerts");

    fireEvent.click(
      within(form).getByRole("button", { name: "Replace the address" }),
    );

    const address = within(form).getByLabelText("Webhook URL");
    expect(address).toHaveAttribute("type", "url");
    expect(address).toBeRequired();
    expect(address).toHaveValue("");
    expect(within(form).queryByText(SHOWN_ADDRESS)).not.toBeInTheDocument();
    expect(
      within(form).getByRole("button", { name: "Save alert" }),
    ).toBeDisabled();
  });

  it("sends the new address when one is typed, and the token is found nowhere on the page afterwards", async () => {
    const server = serve({ [`PUT ${CHANNELS}/1`]: addressReplaced });
    render(<AlertsTab />, { wrapper });
    const form = await openEditForm("Discord alerts");

    fireEvent.click(
      within(form).getByRole("button", { name: "Replace the address" }),
    );
    fireEvent.change(within(form).getByLabelText("Webhook URL"), {
      target: { value: NEW_ADDRESS },
    });
    save(form);

    await vi.waitFor(() =>
      expect(
        screen.queryByRole("region", { name: "Edit alert" }),
      ).not.toBeInTheDocument(),
    );
    const sent = server.sent("PUT", `${CHANNELS}/1`);
    expect(sent.map((r) => r.body)).toEqual([
      {
        label: "Discord alerts",
        provider: "discord",
        url: NEW_ADDRESS,
        events: ["job_failed", "processing_job_failed"],
        enabled: true,
        csrf_token: token,
      },
    ]);
    expect(document.body.innerHTML).not.toContain(NEW_TOKEN);
    expect(document.body.innerHTML).not.toContain(SAVED_TOKEN);
    expect(everythingKept()).not.toContain(NEW_TOKEN);
    expect(everythingKept()).not.toContain(SAVED_TOKEN);
    const sentWithToken = server.requests.filter((request) =>
      JSON.stringify(request.body ?? "").includes(NEW_TOKEN),
    );
    expect(sentWithToken.map((r) => `${r.method} ${r.path}`)).toEqual([
      `PUT ${CHANNELS}/1`,
    ]);
  });

  it("goes back to the saved address when Keep the saved address is pressed, and sends none", async () => {
    const server = serve({
      [`PUT ${CHANNELS}/1`]: savedKeepingTheAddress,
    });
    render(<AlertsTab />, { wrapper });
    const form = await openEditForm("Discord alerts");
    fireEvent.click(
      within(form).getByRole("button", { name: "Replace the address" }),
    );
    fireEvent.change(within(form).getByLabelText("Webhook URL"), {
      target: { value: NEW_ADDRESS },
    });

    fireEvent.click(
      within(form).getByRole("button", { name: "Keep the saved address" }),
    );

    expect(within(form).getByText(SHOWN_ADDRESS)).toBeInTheDocument();
    expect(
      within(form).queryByLabelText("Webhook URL"),
    ).not.toBeInTheDocument();
    save(form);
    await vi.waitFor(() =>
      expect(server.sent("PUT", `${CHANNELS}/1`)).toHaveLength(1),
    );
    expect(server.sent("PUT", `${CHANNELS}/1`)[0].body).not.toHaveProperty(
      "url",
    );
  });

  it("says an alert already sends to the address when the new one is taken, and stays open", async () => {
    serve({ [`PUT ${CHANNELS}/1`]: addressInUse });
    render(<AlertsTab />, { wrapper });
    const form = await openEditForm("Discord alerts");
    fireEvent.click(
      within(form).getByRole("button", { name: "Replace the address" }),
    );
    fireEvent.change(within(form).getByLabelText("Webhook URL"), {
      target: { value: OTHER_ALERTS_ADDRESS },
    });

    save(form);

    expect(await within(form).findByRole("alert")).toHaveTextContent(
      "An alert already sends to that address.",
    );
    expect(within(form).getByLabelText("Webhook URL")).toHaveValue(
      OTHER_ALERTS_ADDRESS,
    );
  });
});

describe("an event tick in the table", () => {
  it("saves the alert without sending its address", async () => {
    const server = serve({
      [`GET ${CHANNELS}`]: [twoAlerts, twoAlertsAfterTick],
      [`PUT ${CHANNELS}/1`]: tickSaved,
    });
    render(<AlertsTab />, { wrapper });
    const row = await rowOf("Discord alerts");
    const finished = within(row).getByLabelText(
      "Discord alerts: A file finished",
    );

    fireEvent.click(finished);

    await vi.waitFor(() => expect(finished).toBeChecked());
    expect(server.sent("PUT", `${CHANNELS}/1`).map((r) => r.body)).toEqual([
      {
        label: "Discord alerts",
        provider: "discord",
        events: [
          "job_failed",
          "processing_job_failed",
          "processing_job_completed",
        ],
        enabled: true,
        csrf_token: token,
      },
    ]);
  });
});

describe("the Add alert form", () => {
  it("says an alert already sends to the address, keeps the form open with what was typed, and sends it once", async () => {
    const server = serve({ [`POST ${CHANNELS}`]: addressInUse });
    render(<AlertsTab />, { wrapper });
    fireEvent.click(await screen.findByRole("button", { name: "Add alert" }));
    const form = screen.getByRole("dialog", { name: "New alert" });

    fireEvent.change(within(form).getByLabelText("Label"), {
      target: { value: "Again" },
    });
    fireEvent.change(within(form).getByLabelText("Webhook URL"), {
      target: { value: OTHER_ALERTS_ADDRESS },
    });
    fireEvent.click(within(form).getByRole("button", { name: "Save alert" }));

    expect(await within(form).findByRole("alert")).toHaveTextContent(
      "An alert already sends to that address.",
    );
    expect(within(form).getByLabelText("Label")).toHaveValue("Again");
    expect(within(form).getByLabelText("Webhook URL")).toHaveValue(
      OTHER_ALERTS_ADDRESS,
    );
    expect(server.sent("POST", CHANNELS)).toHaveLength(1);
  });
});
