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

import { settingsKeys } from "../../../../lib/settings/query-keys";
import { adminMe, csrfToken } from "../../../../test/recorded-admin-session";
import {
  serveRecordedReplies,
  type RecordedReply,
  type RecordedRoutes,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import {
  editRefusedForItsAddress,
  noChannels,
  oneChannel,
  twoChannels,
} from "./alert-replies";
import { AlertsTab } from "./alerts-tab";

const CHANNELS = "/api/v1/suite/notification-channels";

// No recording of the server refusing the list to a viewer exists yet. The reply is the server's own words for an
// account its rule refuses ("Forbidden.", in apps/server ApiRequest.cs) in the `detail` field every recorded refusal uses.
const refusedToAViewer: RecordedReply = {
  status: 403,
  body: '{"detail":"Forbidden."}',
};

const viewerMe: RecordedReply = {
  status: 200,
  body: adminMe.body.replace('"role":"admin"', '"role":"viewer"'),
};

let weir: RecordedWeir | undefined;
let client: QueryClient;

afterEach(() => {
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  vi.unstubAllGlobals();
});

function serve(routes: RecordedRoutes): RecordedWeir {
  weir = serveRecordedReplies({
    "GET /api/v1/auth/csrf": csrfToken,
    "GET /api/v1/auth/me": adminMe,
    ...routes,
  });
  return weir;
}

function renderTab(): void {
  client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  render(
    <QueryClientProvider client={client}>
      <AlertsTab />
    </QueryClientProvider>,
  );
}

async function rowOf(label: string): Promise<HTMLElement> {
  const row = (await screen.findByText(label)).closest("tr");
  if (!(row instanceof HTMLElement)) throw new Error(`no row for ${label}`);
  return row;
}

describe("a viewer opening Alerts", () => {
  it("is told alerts are for operators and admins, not that Weir failed to load them", async () => {
    serve({
      "GET /api/v1/auth/me": viewerMe,
      [`GET ${CHANNELS}`]: refusedToAViewer,
    });
    renderTab();

    expect(await screen.findByTestId("alerts-need-operator")).toHaveTextContent(
      "Only an operator or an admin can see alerts.",
    );
    expect(screen.queryByTestId("settings-load-error")).not.toBeInTheDocument();
  });
});

describe("the last event of an alert", () => {
  it("cannot be unticked, and the alert says it needs one", async () => {
    const server = serve({ [`GET ${CHANNELS}`]: twoChannels });
    renderTab();
    const row = await rowOf("Ops webhook");
    const only = within(row).getByLabelText(
      "Ops webhook: Anything failed for good",
    );
    expect(only).toBeChecked();

    fireEvent.click(only);

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Ops webhook needs at least one event. Switch the alert off in Edit instead.",
    );
    expect(only).toBeChecked();
    expect(server.sent("PUT", `${CHANNELS}/2`)).toEqual([]);
  });

  it("says why a tick could not be saved, and leaves the box as it was saved", async () => {
    serve({
      [`GET ${CHANNELS}`]: oneChannel,
      [`PUT ${CHANNELS}/1`]: editRefusedForItsAddress,
    });
    renderTab();
    const row = await rowOf("Discord alerts");
    const box = within(row).getByLabelText("Discord alerts: A file finished");
    expect(box).not.toBeChecked();

    fireEvent.click(box);

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Blocked provider URL host: 127.0.0.1.",
    );
    await waitFor(() => expect(box).not.toBeChecked());
  });
});

describe("editing an alert", () => {
  it("opens the alert in a form filled in with what it has, and holds Add alert and the other Edit buttons while it is open", async () => {
    serve({ [`GET ${CHANNELS}`]: twoChannels });
    renderTab();
    const row = await rowOf("Discord alerts");

    fireEvent.click(within(row).getByRole("button", { name: "Edit" }));

    const form = await screen.findByRole("region", { name: "Edit alert" });
    expect(within(form).getByLabelText("Label")).toHaveValue("Discord alerts");
    expect(within(form).getByLabelText("Provider")).toHaveValue("discord");
    expect(within(form).getByLabelText("Enabled")).toBeChecked();
    expect(screen.getByRole("button", { name: /Add alert/ })).toBeDisabled();
    const others = (await rowOf("Ops webhook")).querySelectorAll("button");
    const edit = Array.from(others).find((b) => b.textContent === "Edit");
    expect(edit).toBeDisabled();
  });

  it("lets the other alert be edited once the first form is closed", async () => {
    serve({ [`GET ${CHANNELS}`]: twoChannels });
    renderTab();
    fireEvent.click(
      within(await rowOf("Discord alerts")).getByRole("button", {
        name: "Edit",
      }),
    );
    const form = await screen.findByRole("region", { name: "Edit alert" });

    fireEvent.click(within(form).getByRole("button", { name: "Cancel" }));

    const edit = within(await rowOf("Ops webhook")).getByRole("button", {
      name: "Edit",
    });
    expect(edit).toBeEnabled();
    expect(screen.getByRole("button", { name: /Add alert/ })).toBeEnabled();
  });
});

describe("an alert changed from somewhere else", () => {
  async function refreshFromElsewhere(): Promise<void> {
    await act(async () => {
      await client.invalidateQueries({
        queryKey: settingsKeys.notificationChannels,
      });
    });
  }

  it("shows on the open tab when it is added, changed or removed, with no reload", async () => {
    serve({ [`GET ${CHANNELS}`]: [oneChannel, twoChannels, noChannels] });
    renderTab();
    await rowOf("Discord alerts");
    expect(screen.queryByText("Ops webhook")).not.toBeInTheDocument();

    await refreshFromElsewhere();
    expect(await screen.findByText("Ops webhook")).toBeInTheDocument();

    await refreshFromElsewhere();
    expect(await screen.findByTestId("alerts-empty")).toBeInTheDocument();
    expect(screen.queryByText("Discord alerts")).not.toBeInTheDocument();
  });
});
