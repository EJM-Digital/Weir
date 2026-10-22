import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, within } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { adminMe, csrfToken } from "../../../../test/recorded-admin-session";
import type { NotificationChannelListOut } from "../../../../lib/settings/types";
import {
  serveRecordedReplies,
  type RecordedReply,
  type RecordedRoutes,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import { AlertsTab } from "./alerts-tab";
import {
  addRefusedForItsAddress,
  afterEdit,
  afterTickOnSwitchedOff,
  afterTick,
  afterUntick,
  channelAdded,
  editRefusedForItsAddress,
  editSaved,
  firstOfTwoSwitchedOff,
  noChannels,
  oneChannel,
  testAlertGone,
  testCouldNotResolve,
  testCouldNotResolveToo,
  testDelivered,
  tickOnSwitchedOffSaved,
  tickSaved,
  twoChannels,
  untickSaved,
} from "./alert-replies";

const CHANNELS = "/api/v1/suite/notification-channels";

const token = (JSON.parse(csrfToken.body) as { csrf_token: string }).csrf_token;
const [discord] = (JSON.parse(oneChannel.body) as NotificationChannelListOut)
  .items;
/** The address typed when the first alert was recorded; the server shows it with its token hidden. */
const DISCORD_ADDRESS =
  "https://hooks.weir-recording.invalid/api/webhooks/1034598712/s3cr3t-T0ken_abc";
const WEBHOOK_URL = "https://hooks.weir-recording.invalid/pager/9f2c7a1e-token";
const ADDRESS_ON_THIS_MACHINE = "http://127.0.0.1:8080/hook";

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

function serve(routes: RecordedRoutes): RecordedWeir {
  weir = serveRecordedReplies({
    "GET /api/v1/auth/csrf": csrfToken,
    "GET /api/v1/auth/me": adminMe,
    ...routes,
  });
  return weir;
}

/** A route that holds its answer until the test lets it go, so what the page shows while it waits can be read. */
function held(reply: RecordedReply | RecordedReply[]) {
  const replies = Array.isArray(reply) ? reply : [reply];
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  let asked = 0;
  return {
    release,
    route: async () => {
      await gate;
      return replies[Math.min(asked++, replies.length - 1)];
    },
  };
}

async function rowOf(label: string): Promise<HTMLElement> {
  const row = (await screen.findByText(label)).closest("tr");
  if (!(row instanceof HTMLElement)) throw new Error(`no row for ${label}`);
  return row;
}

async function openAddForm(): Promise<HTMLElement> {
  fireEvent.click(await screen.findByRole("button", { name: "Add alert" }));
  return screen.getByRole("dialog", { name: "New alert" });
}

async function openEditForm(label: string): Promise<HTMLElement> {
  const row = await rowOf(label);
  fireEvent.click(within(row).getByRole("button", { name: "Edit" }));
  return screen.findByRole("region", { name: "Edit alert" });
}

function replaceTheAddress(form: HTMLElement): void {
  fireEvent.click(
    within(form).getByRole("button", { name: "Replace the address" }),
  );
}

function fill(form: HTMLElement, label: string, value: string): void {
  fireEvent.change(within(form).getByLabelText(label), { target: { value } });
}

describe("the Add alert form", () => {
  it("starts with one trigger event and Enabled on, and cannot be saved until it has a label and an address", async () => {
    serve({ [`GET ${CHANNELS}`]: noChannels });
    render(<AlertsTab />, { wrapper });

    const form = await openAddForm();

    const label = within(form).getByLabelText("Label");
    expect(label).toBeRequired();
    expect(label).toHaveAttribute("maxlength", "255");
    const address = within(form).getByLabelText("Webhook URL");
    expect(address).toBeRequired();
    expect(address).toHaveAttribute("type", "url");
    const provider = within(form).getByLabelText("Provider");
    expect(
      within(provider)
        .getAllByRole("option")
        .map((option) => option.textContent),
    ).toEqual(["Generic webhook (JSON POST)", "Discord webhook"]);
    expect(provider).toHaveValue("webhook");
    expect(
      within(form)
        .getAllByRole("checkbox")
        .map((box) => [
          box.parentElement?.textContent,
          (box as HTMLInputElement).checked,
        ]),
    ).toEqual([
      ["A file finished", false],
      ["A file failed for good", false],
      ["Anything failed for good", true],
      ["Anything finished", false],
      ["Enabled", true],
    ]);

    const save = within(form).getByRole("button", { name: "Save alert" });
    expect(save).toBeDisabled();
    fill(form, "Label", "Ops webhook");
    expect(save).toBeDisabled();
    fill(form, "Webhook URL", WEBHOOK_URL);
    expect(save).toBeEnabled();
  });

  it("will not save a label that is only spaces", async () => {
    serve({ [`GET ${CHANNELS}`]: noChannels });
    render(<AlertsTab />, { wrapper });
    const form = await openAddForm();

    fill(form, "Label", "   ");
    fill(form, "Webhook URL", WEBHOOK_URL);

    expect(
      within(form).getByRole("button", { name: "Save alert" }),
    ).toBeDisabled();
  });

  it("cannot be saved once the last trigger event is unticked, and can again when one is ticked", async () => {
    serve({ [`GET ${CHANNELS}`]: noChannels });
    render(<AlertsTab />, { wrapper });
    const form = await openAddForm();
    fill(form, "Label", "Ops webhook");
    fill(form, "Webhook URL", WEBHOOK_URL);
    const save = within(form).getByRole("button", { name: "Save alert" });
    expect(save).toBeEnabled();

    fireEvent.click(within(form).getByLabelText("Anything failed for good"));
    expect(save).toBeDisabled();
    fireEvent.click(within(form).getByLabelText("A file finished"));

    expect(save).toBeEnabled();
  });

  it("sends the label, provider, address, events and Enabled, shuts the form and shows the new alert", async () => {
    const server = serve({
      [`GET ${CHANNELS}`]: [noChannels, oneChannel],
      [`POST ${CHANNELS}`]: channelAdded,
    });
    render(<AlertsTab />, { wrapper });
    const form = await openAddForm();

    fill(form, "Label", discord.label);
    fill(form, "Provider", "discord");
    fill(form, "Webhook URL", DISCORD_ADDRESS);
    fireEvent.click(within(form).getByLabelText("A file failed for good"));
    fireEvent.click(within(form).getByRole("button", { name: "Save alert" }));

    const row = await rowOf(discord.label);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(within(row).getByText("discord")).toBeInTheDocument();
    expect(server.sent("POST", CHANNELS).map((r) => r.body)).toEqual([
      {
        label: discord.label,
        provider: "discord",
        url: DISCORD_ADDRESS,
        events: ["job_failed", "processing_job_failed"],
        enabled: true,
        csrf_token: token,
      },
    ]);
  });

  it("sends Enabled as off when it is unticked", async () => {
    const server = serve({
      [`GET ${CHANNELS}`]: [noChannels, oneChannel],
      [`POST ${CHANNELS}`]: channelAdded,
    });
    render(<AlertsTab />, { wrapper });
    const form = await openAddForm();

    fill(form, "Label", discord.label);
    fill(form, "Webhook URL", DISCORD_ADDRESS);
    fireEvent.click(within(form).getByLabelText("Enabled"));
    fireEvent.click(within(form).getByRole("button", { name: "Save alert" }));

    await rowOf(discord.label);
    expect(server.sent("POST", CHANNELS)[0].body).toMatchObject({
      enabled: false,
    });
  });

  it("says why the server refused, keeps the form open with what was typed, and sends nothing else", async () => {
    const server = serve({
      [`GET ${CHANNELS}`]: noChannels,
      [`POST ${CHANNELS}`]: addRefusedForItsAddress,
    });
    render(<AlertsTab />, { wrapper });
    const form = await openAddForm();

    fill(form, "Label", "Ops webhook");
    fill(form, "Webhook URL", ADDRESS_ON_THIS_MACHINE);
    fireEvent.click(within(form).getByRole("button", { name: "Save alert" }));

    expect(await within(form).findByRole("alert")).toHaveTextContent(
      "Blocked provider URL host: 127.0.0.1.",
    );
    expect(screen.getByRole("dialog", { name: "New alert" })).toBe(form);
    expect(within(form).getByLabelText("Label")).toHaveValue("Ops webhook");
    expect(within(form).getByLabelText("Webhook URL")).toHaveValue(
      ADDRESS_ON_THIS_MACHINE,
    );
    expect(
      within(form).getByRole("button", { name: "Save alert" }),
    ).toBeEnabled();
    expect(server.sent("POST", CHANNELS)).toHaveLength(1);
  });

  it("says Saving… and holds every field still while the server answers", async () => {
    const answer = held(channelAdded);
    serve({
      [`GET ${CHANNELS}`]: [noChannels, oneChannel],
      [`POST ${CHANNELS}`]: answer.route,
    });
    render(<AlertsTab />, { wrapper });
    const form = await openAddForm();
    fill(form, "Label", discord.label);
    fill(form, "Webhook URL", DISCORD_ADDRESS);
    fireEvent.click(within(form).getByRole("button", { name: "Save alert" }));

    expect(
      await within(form).findByRole("button", { name: "Saving…" }),
    ).toBeDisabled();
    for (const field of [
      ...within(form).getAllByRole("textbox"),
      within(form).getByLabelText("Provider"),
      ...within(form).getAllByRole("checkbox"),
      within(form).getByRole("button", { name: "Cancel" }),
    ]) {
      expect(field).toBeDisabled();
    }
    answer.release();

    await rowOf(discord.label);
  });
});

describe("the Edit alert form", () => {
  it("sends every changed field, shuts the form, and the row shows the change", async () => {
    const server = serve({
      [`GET ${CHANNELS}`]: [oneChannel, afterEdit],
      [`PUT ${CHANNELS}/1`]: editSaved,
    });
    render(<AlertsTab />, { wrapper });
    const form = await openEditForm(discord.label);

    fill(form, "Label", "Pager bridge");
    fill(form, "Provider", "webhook");
    replaceTheAddress(form);
    fill(form, "Webhook URL", WEBHOOK_URL);
    fireEvent.click(within(form).getByLabelText("Anything failed for good"));
    fireEvent.click(within(form).getByLabelText("A file finished"));
    fireEvent.click(within(form).getByLabelText("Enabled"));
    fireEvent.click(within(form).getByRole("button", { name: "Save alert" }));

    const row = await rowOf("Pager bridge");
    expect(
      screen.queryByRole("region", { name: "Edit alert" }),
    ).not.toBeInTheDocument();
    expect(within(row).getByText("webhook")).toBeInTheDocument();
    expect(within(row).getByText("Disabled")).toBeInTheDocument();
    expect(server.sent("PUT", `${CHANNELS}/1`).map((r) => r.body)).toEqual([
      {
        label: "Pager bridge",
        provider: "webhook",
        url: WEBHOOK_URL,
        events: ["processing_job_failed", "processing_job_completed"],
        enabled: false,
        csrf_token: token,
      },
    ]);
  });

  it("says why the server refused and keeps the form open with what was typed", async () => {
    serve({
      [`GET ${CHANNELS}`]: oneChannel,
      [`PUT ${CHANNELS}/1`]: editRefusedForItsAddress,
    });
    render(<AlertsTab />, { wrapper });
    const form = await openEditForm(discord.label);

    replaceTheAddress(form);
    fill(form, "Webhook URL", ADDRESS_ON_THIS_MACHINE);
    fireEvent.click(within(form).getByRole("button", { name: "Save alert" }));

    expect(await within(form).findByRole("alert")).toHaveTextContent(
      "Blocked provider URL host: 127.0.0.1.",
    );
    expect(within(form).getByLabelText("Webhook URL")).toHaveValue(
      ADDRESS_ON_THIS_MACHINE,
    );
  });

  it("throws away what was typed when it is cancelled and left, and sends nothing", async () => {
    const server = serve({ [`GET ${CHANNELS}`]: oneChannel });
    render(<AlertsTab />, { wrapper });
    const form = await openEditForm(discord.label);

    fill(form, "Label", "Something else");
    fireEvent.click(within(form).getByRole("button", { name: "Cancel" }));
    fireEvent.click(
      await screen.findByTestId("settings-unsaved-changes-confirm"),
    );

    const row = await rowOf(discord.label);
    expect(within(row).getByText(discord.provider)).toBeInTheDocument();
    expect(screen.queryByText("Something else")).not.toBeInTheDocument();
    expect(server.sent("PUT", `${CHANNELS}/1`)).toEqual([]);
  });
});

describe("an event tick in the table", () => {
  it("saves the whole alert with the event added at once, and the box follows the server", async () => {
    const answer = held(tickSaved);
    const server = serve({
      [`GET ${CHANNELS}`]: [oneChannel, afterTick],
      [`PUT ${CHANNELS}/1`]: answer.route,
    });
    render(<AlertsTab />, { wrapper });
    const row = await rowOf(discord.label);
    const finished = within(row).getByLabelText(
      `${discord.label}: A file finished`,
    );
    expect(finished).not.toBeChecked();

    fireEvent.click(finished);

    await vi.waitFor(() => expect(finished).toBeDisabled());
    for (const box of within(row).getAllByRole("checkbox")) {
      expect(box).toBeDisabled();
    }
    answer.release();
    await vi.waitFor(() => expect(finished).toBeChecked());
    expect(finished).toBeEnabled();
    expect(server.sent("PUT", `${CHANNELS}/1`).map((r) => r.body)).toEqual([
      {
        label: discord.label,
        provider: discord.provider,
        events: [...discord.events, "processing_job_completed"],
        enabled: true,
        csrf_token: token,
      },
    ]);
  });

  it("saves the alert without an event that is unticked", async () => {
    const server = serve({
      [`GET ${CHANNELS}`]: [afterTick, afterUntick],
      [`PUT ${CHANNELS}/1`]: untickSaved,
    });
    render(<AlertsTab />, { wrapper });
    const row = await rowOf(discord.label);
    const failed = within(row).getByLabelText(
      `${discord.label}: A file failed for good`,
    );
    expect(failed).toBeChecked();

    fireEvent.click(failed);

    await vi.waitFor(() => expect(failed).not.toBeChecked());
    expect(server.sent("PUT", `${CHANNELS}/1`)[0].body).toMatchObject({
      events: ["job_failed", "processing_job_completed"],
    });
  });
});

describe("an event tick on an alert that is switched off", () => {
  it("keeps the alert switched off", async () => {
    const server = serve({
      [`GET ${CHANNELS}`]: [firstOfTwoSwitchedOff, afterTickOnSwitchedOff],
      [`PUT ${CHANNELS}/1`]: tickOnSwitchedOffSaved,
    });
    render(<AlertsTab />, { wrapper });
    const row = await rowOf("Pager bridge");
    expect(within(row).getByText("Disabled")).toBeInTheDocument();
    const anything = within(row).getByLabelText(
      "Pager bridge: Anything failed for good",
    );

    fireEvent.click(anything);

    await vi.waitFor(() => expect(anything).toBeChecked());
    expect(server.sent("PUT", `${CHANNELS}/1`).map((r) => r.body)).toEqual([
      {
        label: "Pager bridge",
        provider: "webhook",
        events: [
          "processing_job_failed",
          "processing_job_completed",
          "job_failed",
        ],
        enabled: false,
        csrf_token: token,
      },
    ]);
    expect(within(row).getByText("Disabled")).toBeInTheDocument();
  });
});

describe("Send test", () => {
  it("says Testing… while it waits, then why the test failed, beside that alert only", async () => {
    const answer = held(testCouldNotResolveToo);
    const server = serve({
      [`GET ${CHANNELS}`]: twoChannels,
      [`POST ${CHANNELS}/2/test`]: answer.route,
    });
    render(<AlertsTab />, { wrapper });
    const row = await rowOf("Ops webhook");

    fireEvent.click(within(row).getByRole("button", { name: "Send test" }));

    expect(
      await within(row).findByRole("button", { name: "Testing…" }),
    ).toBeDisabled();
    answer.release();
    expect(await within(row).findByRole("alert")).toHaveTextContent(
      "Test failed: Weir could not resolve the notification destination.",
    );
    expect(
      within(await rowOf(discord.label)).queryByRole("alert"),
    ).not.toBeInTheDocument();
    expect(
      server.sent("POST", `${CHANNELS}/2/test`).map((r) => r.body),
    ).toEqual([{ csrf_token: token }]);
    expect(server.sent("POST", `${CHANNELS}/1/test`)).toEqual([]);
  });

  it("says the test alert was sent when the server delivered it", async () => {
    serve({
      [`GET ${CHANNELS}`]: twoChannels,
      [`POST ${CHANNELS}/1/test`]: testDelivered,
    });
    render(<AlertsTab />, { wrapper });
    const row = await rowOf(discord.label);

    fireEvent.click(within(row).getByRole("button", { name: "Send test" }));

    expect(await within(row).findByRole("alert")).toHaveTextContent(
      "Test alert sent successfully.",
    );
  });

  it("shows a request that fails as a failed test", async () => {
    serve({
      [`GET ${CHANNELS}`]: twoChannels,
      [`POST ${CHANNELS}/1/test`]: testAlertGone,
    });
    render(<AlertsTab />, { wrapper });
    const row = await rowOf(discord.label);

    fireEvent.click(within(row).getByRole("button", { name: "Send test" }));

    expect(await within(row).findByRole("alert")).toHaveTextContent(
      "Test failed: Notification channel not found.",
    );
  });

  it("shows a request that never arrives as a failed test", async () => {
    serve({
      [`GET ${CHANNELS}`]: twoChannels,
      [`POST ${CHANNELS}/1/test`]: () =>
        Promise.reject(new TypeError("Failed to fetch")),
    });
    render(<AlertsTab />, { wrapper });
    const row = await rowOf(discord.label);

    fireEvent.click(within(row).getByRole("button", { name: "Send test" }));

    expect(await within(row).findByRole("alert")).toHaveTextContent(
      "Test failed: Can't reach Weir. Check it's still running.",
    );
  });

  it("clears the last answer when the next test starts", async () => {
    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    let asked = 0;
    serve({
      [`GET ${CHANNELS}`]: twoChannels,
      [`POST ${CHANNELS}/1/test`]: async () => {
        if (asked++ === 0) return testCouldNotResolve;
        await gate;
        return testDelivered;
      },
    });
    render(<AlertsTab />, { wrapper });
    const row = await rowOf(discord.label);
    fireEvent.click(within(row).getByRole("button", { name: "Send test" }));
    expect(await within(row).findByRole("alert")).toHaveTextContent(
      "Test failed",
    );

    fireEvent.click(within(row).getByRole("button", { name: "Send test" }));

    expect(
      await within(row).findByRole("button", { name: "Testing…" }),
    ).toBeDisabled();
    expect(within(row).queryByRole("alert")).not.toBeInTheDocument();
    release();
    expect(await within(row).findByRole("alert")).toHaveTextContent(
      "Test alert sent successfully.",
    );
  });
});

describe("a test alert that fails without saying why", () => {
  it("says Unknown error", async () => {
    serve({
      [`GET ${CHANNELS}`]: twoChannels,
      [`POST ${CHANNELS}/1/test`]: {
        status: 200,
        body: '{"ok":false,"error":null}',
      },
    });
    render(<AlertsTab />, { wrapper });
    const row = await rowOf(discord.label);

    fireEvent.click(within(row).getByRole("button", { name: "Send test" }));

    expect(await within(row).findByRole("alert")).toHaveTextContent(
      "Test failed: Unknown error",
    );
  });
});
