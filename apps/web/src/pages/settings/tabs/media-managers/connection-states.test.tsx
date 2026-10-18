import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import { heldReply } from "../../../../test/held-reply";
import { adminMe, csrfToken } from "../../../../test/recorded-admin-session";
import { recordedReply } from "../../../../test/recorded-reply";
import { systemReplies } from "../../../../test/recorded-system";
import {
  serveRecordedReplies,
  type RecordedRoutes,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import {
  delunoAdded,
  delunoListedWithoutSecret,
  delunoSecretCreated,
  noConnections,
  WEBHOOK_SECRET,
} from "./connection-replies";
import { MediaManagersTab } from "./media-managers-tab";

const CONNECTIONS = "/api/v1/media-managers/connections";
const SECRET_PROMPT = "media-manager-new-secret-prompt";

const recordedList = recordedReply(`GET ${CONNECTIONS}`);
const recordedEdit = recordedReply(`PUT ${CONNECTIONS}/1`);
const noLibraries = { status: 200, body: "[]" };

/** The recorded settings, with the time zone System › About lets the owner choose. */
function settingsIn(zone: string) {
  return {
    status: 200,
    body: JSON.stringify({
      ...JSON.parse(systemReplies.settings.body),
      app_timezone: zone,
    }),
  };
}

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
  return (
    <MemoryRouter>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </MemoryRouter>
  );
}

function serve(routes: RecordedRoutes): RecordedWeir {
  weir = serveRecordedReplies({
    "GET /api/v1/auth/csrf": csrfToken,
    "GET /api/v1/auth/me": adminMe,
    "GET /api/v1/processing/libraries": noLibraries,
    [`GET ${CONNECTIONS}/1/folder-chain`]: { status: 200, body: "[]" },
    "GET /api/v1/suite/settings": systemReplies.settings,
    ...routes,
  });
  return weir;
}

describe("the first media manager", () => {
  it("is added from the button in the empty state, which opens the add drawer", async () => {
    serve({ [`GET ${CONNECTIONS}`]: noConnections });
    render(<MediaManagersTab />, { wrapper });

    fireEvent.click(
      await screen.findByRole("button", {
        name: "Add your first media manager",
      }),
    );

    expect(screen.getByTestId("media-manager-add-panel")).toBeInTheDocument();
    expect(screen.getByTestId("media-manager-kind")).toHaveFocus();
  });
});

describe("adding a media manager", () => {
  it("says Adding… and cannot be submitted again while the server answers", async () => {
    const answer = heldReply(delunoAdded);
    const server = serve({
      [`GET ${CONNECTIONS}`]: [noConnections, delunoListedWithoutSecret],
      [`POST ${CONNECTIONS}`]: answer.route,
    });
    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByTestId("media-manager-add"));
    fireEvent.change(screen.getByTestId("media-manager-base-url"), {
      target: { value: "http://localhost:5099" },
    });

    fireEvent.click(screen.getByTestId("media-manager-save"));
    const pending = await screen.findByRole("button", { name: "Adding…" });
    fireEvent.click(pending);

    expect(pending).toBeDisabled();
    expect(server.sent("POST", CONNECTIONS)).toHaveLength(1);
    answer.release();
    expect(await screen.findByTestId(SECRET_PROMPT)).toBeInTheDocument();
    expect(server.sent("POST", CONNECTIONS)).toHaveLength(1);
  });
});

describe("the secret offered for a new media manager", () => {
  async function addOne() {
    const answer = heldReply(delunoSecretCreated);
    serve({
      [`GET ${CONNECTIONS}`]: [noConnections, delunoListedWithoutSecret],
      [`POST ${CONNECTIONS}`]: delunoAdded,
      [`POST ${CONNECTIONS}/1/webhook-secret`]: answer.route,
    });
    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByTestId("media-manager-add"));
    fireEvent.change(screen.getByTestId("media-manager-base-url"), {
      target: { value: "http://localhost:5099" },
    });
    fireEvent.click(screen.getByTestId("media-manager-save"));
    fireEvent.click(
      await screen.findByTestId("media-manager-new-secret-create"),
    );
    return answer;
  }

  it("says Creating… and holds both buttons still while the secret is made", async () => {
    const answer = await addOne();

    const prompt = screen.getByTestId(SECRET_PROMPT);
    expect(
      await within(prompt).findByRole("button", { name: "Creating…" }),
    ).toBeDisabled();
    expect(
      within(prompt).getByRole("button", { name: "Not now" }),
    ).toBeDisabled();
    answer.release();
    expect(await screen.findByTestId("media-manager-secret")).toHaveTextContent(
      WEBHOOK_SECRET,
    );
  });

  it("goes away when Done is pressed after the secret is shown", async () => {
    const answer = await addOne();
    answer.release();
    await screen.findByTestId("media-manager-secret");

    fireEvent.click(
      within(screen.getByTestId(SECRET_PROMPT)).getByRole("button", {
        name: "Done",
      }),
    );

    expect(screen.queryByTestId(SECRET_PROMPT)).not.toBeInTheDocument();
    expect(
      screen.queryByTestId("media-manager-secret"),
    ).not.toBeInTheDocument();
  });
});

describe("the list of media managers", () => {
  it("counts them, says how often Weir checks, and says changes save as they are made", async () => {
    serve({ [`GET ${CONNECTIONS}`]: recordedList });
    render(<MediaManagersTab />, { wrapper });

    const list = await screen.findByText(
      "1 media manager. Weir checks each one every minute.",
    );
    const panel = list.closest("section");
    expect(panel).not.toBeNull();
    expect(
      within(panel as HTMLElement).getByTestId("settings-save-model"),
    ).toHaveTextContent("Changes save as soon as you make them.");
  });

  it("shows when a media manager was last checked in the time zone chosen for Weir", async () => {
    serve({
      [`GET ${CONNECTIONS}`]: recordedList,
      "GET /api/v1/suite/settings": settingsIn("Pacific/Auckland"),
    });
    render(<MediaManagersTab />, { wrapper });

    const status = await screen.findByTestId("media-manager-status");
    await waitFor(() =>
      expect(status).toHaveTextContent(/Last checked:.*\b7:15/),
    );
    // The check was at 06:15 UTC, so the same clock reading would mean the zone was ignored.
    expect(status).not.toHaveTextContent(/\b6:15/);
    expect(status).not.toHaveTextContent("within a minute");
  });

  it("tells a media manager that has not been checked yet that Weir will check it within a minute", async () => {
    serve({ [`GET ${CONNECTIONS}`]: delunoListedWithoutSecret });
    render(<MediaManagersTab />, { wrapper });

    const status = await screen.findByTestId("media-manager-status");
    expect(status).toHaveTextContent("Last checked: never");
    expect(status).toHaveTextContent(
      "Weir checks it within a minute, or press Test.",
    );
  });
});

describe("saving an edit to a media manager", () => {
  it("says Saving… and holds Save and Cancel still while the server answers", async () => {
    const answer = heldReply(recordedEdit);
    const server = serve({
      [`GET ${CONNECTIONS}`]: recordedList,
      [`PUT ${CONNECTIONS}/1`]: answer.route,
    });
    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByTestId("media-manager-edit"));
    fireEvent.change(screen.getByTestId("media-manager-edit-base-url"), {
      target: { value: "http://localhost:5100" },
    });

    fireEvent.click(screen.getByTestId("media-manager-edit-save"));

    const saving = await screen.findByRole("button", { name: "Saving…" });
    expect(saving).toBeDisabled();
    expect(screen.getByTestId("media-manager-edit-cancel")).toBeDisabled();
    expect(server.sent("PUT", `${CONNECTIONS}/1`)).toHaveLength(1);
    answer.release();
    await waitFor(() =>
      expect(
        screen.queryByTestId("media-manager-edit-form"),
      ).not.toBeInTheDocument(),
    );
  });
});

describe("the check of a media manager's workflow folders", () => {
  it("says it is checking while the answer is on its way", async () => {
    const answer = heldReply({ status: 200, body: "[]" });
    serve({
      [`GET ${CONNECTIONS}`]: recordedList,
      [`GET ${CONNECTIONS}/1/folder-chain`]: answer.route,
    });
    render(<MediaManagersTab />, { wrapper });

    expect(
      await screen.findByText("Checking its workflows' folder chains…"),
    ).toBeInTheDocument();
    answer.release();
    await waitFor(() =>
      expect(
        screen.queryByText("Checking its workflows' folder chains…"),
      ).not.toBeInTheDocument(),
    );
  });

  it("says quietly that it could not check, without an alert of its own", async () => {
    serve({
      [`GET ${CONNECTIONS}`]: recordedList,
      [`GET ${CONNECTIONS}/1/folder-chain`]: {
        status: 500,
        body: '{"detail":"Internal Server Error"}',
      },
    });
    render(<MediaManagersTab />, { wrapper });

    const line = await screen.findByText(
      "Weir could not check its workflows' folder chains just now.",
    );
    expect(line).not.toHaveAttribute("role");
  });
});
