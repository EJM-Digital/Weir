import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, within } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
  serveRecordedReplies,
  type RecordedReply,
  type RecordedRoutes,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import {
  addressRefused,
  connectionGone,
  csrfToken,
  delunoAdded,
  delunoListedWithSecret,
  delunoListedWithoutSecret,
  delunoSecretCreated,
  noConnections,
  signedInAdmin,
  WEBHOOK_SECRET,
} from "./connection-replies";
import { MediaManagersTab } from "./media-managers-tab";
import { stubMediaManagersTabNeighbours } from "./stub-media-managers-tab-neighbours";

const CONNECTIONS = "/api/v1/media-managers/connections";
const SECRET = `${CONNECTIONS}/1/webhook-secret`;

const TYPED_KEY = "typed-deluno-api-key";
const SECRET_PROMPT = "media-manager-new-secret-prompt";
const REPLACE_DIALOG = "media-manager-replace-secret-confirm";

const token = (JSON.parse(csrfToken.body) as { csrf_token: string }).csrf_token;

let weir: RecordedWeir | undefined;

beforeEach(stubMediaManagersTabNeighbours);

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
  return (
    <MemoryRouter>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </MemoryRouter>
  );
}

function serve(routes: RecordedRoutes): RecordedWeir {
  weir = serveRecordedReplies({
    "GET /api/v1/auth/csrf": csrfToken,
    "GET /api/v1/auth/me": signedInAdmin,
    ...routes,
  });
  return weir;
}

/** A route that holds its answer until the test lets it go, so what the page shows while it waits can be read. */
function held(reply: RecordedReply) {
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  return {
    release,
    route: async () => {
      await gate;
      return reply;
    },
  };
}

async function openAddPanel(): Promise<HTMLElement> {
  fireEvent.click(await screen.findByTestId("media-manager-add"));
  return screen.getByTestId("media-manager-add-panel");
}

function typeInto(testId: string, value: string): void {
  fireEvent.change(screen.getByTestId(testId), { target: { value } });
}

describe("adding a media manager", () => {
  it("takes the API key in a masked, optional field and sends it in the new connection", async () => {
    const server = serve({
      [`GET ${CONNECTIONS}`]: [noConnections, delunoListedWithoutSecret],
      [`POST ${CONNECTIONS}`]: delunoAdded,
    });
    render(<MediaManagersTab />, { wrapper });
    const panel = await openAddPanel();

    const key = screen.getByTestId("media-manager-api-key");
    expect(key).toHaveAttribute("type", "password");
    expect(key).not.toBeRequired();
    expect(panel).toHaveTextContent(
      "Weir stores this safely and never shows it again.",
    );
    typeInto("media-manager-base-url", "http://localhost:5099");
    typeInto("media-manager-api-key", TYPED_KEY);
    fireEvent.click(screen.getByTestId("media-manager-save"));

    expect(
      await screen.findByTestId("media-manager-new-secret-create"),
    ).toBeInTheDocument();
    expect(server.sent("POST", CONNECTIONS).map((r) => r.body)).toEqual([
      {
        kind: "deluno",
        base_url: "http://localhost:5099",
        api_key: TYPED_KEY,
        nickname: "",
        enabled: true,
        downloaded_scan_enabled: false,
        csrf_token: token,
      },
    ]);
    expect(document.body.innerHTML).not.toContain(TYPED_KEY);
  });

  it("can be added without a key, which is sent as empty", async () => {
    const server = serve({
      [`GET ${CONNECTIONS}`]: [noConnections, delunoListedWithoutSecret],
      [`POST ${CONNECTIONS}`]: delunoAdded,
    });
    render(<MediaManagersTab />, { wrapper });
    await openAddPanel();

    typeInto("media-manager-base-url", "http://localhost:5099");
    expect(screen.getByTestId("media-manager-save")).toBeEnabled();
    fireEvent.click(screen.getByTestId("media-manager-save"));

    await screen.findByTestId("media-manager-new-secret-create");
    expect(server.sent("POST", CONNECTIONS)[0].body).toMatchObject({
      api_key: "",
    });
  });

  it("says why the server refused, and keeps the panel open with what was typed", async () => {
    const server = serve({
      [`GET ${CONNECTIONS}`]: noConnections,
      [`POST ${CONNECTIONS}`]: addressRefused,
    });
    render(<MediaManagersTab />, { wrapper });
    const panel = await openAddPanel();

    typeInto("media-manager-base-url", "not-a-url");
    typeInto("media-manager-api-key", TYPED_KEY);
    fireEvent.click(screen.getByTestId("media-manager-save"));

    expect(await within(panel).findByRole("alert")).toHaveTextContent(
      "That address will not work: URL must be a valid http or https URL.",
    );
    expect(screen.getByTestId("media-manager-add-panel")).toBe(panel);
    expect(screen.getByTestId("media-manager-base-url")).toHaveValue(
      "not-a-url",
    );
    expect(screen.getByTestId("media-manager-api-key")).toHaveValue(TYPED_KEY);
    expect(screen.getByTestId("media-manager-save")).toBeEnabled();
    expect(screen.queryByTestId(SECRET_PROMPT)).not.toBeInTheDocument();
    expect(server.sent("POST", CONNECTIONS)).toHaveLength(1);
  });
});

describe("the secret offered for a new media manager", () => {
  async function addDeluno(routes: RecordedRoutes): Promise<RecordedWeir> {
    const server = serve({
      [`GET ${CONNECTIONS}`]: [noConnections, delunoListedWithoutSecret],
      [`POST ${CONNECTIONS}`]: delunoAdded,
      ...routes,
    });
    render(<MediaManagersTab />, { wrapper });
    await openAddPanel();
    typeInto("media-manager-base-url", "http://localhost:5099");
    fireEvent.click(screen.getByTestId("media-manager-save"));
    await screen.findByTestId("media-manager-new-secret-create");
    return server;
  }

  it("says why it could not be created, and stays so the secret can be asked for again", async () => {
    const server = await addDeluno({ [`POST ${SECRET}`]: connectionGone });

    fireEvent.click(screen.getByTestId("media-manager-new-secret-create"));

    const prompt = screen.getByTestId(SECRET_PROMPT);
    expect(await within(prompt).findByRole("alert")).toHaveTextContent(
      "That media manager connection does not exist.",
    );
    expect(screen.getByTestId("media-manager-new-secret-create")).toBeEnabled();
    expect(
      screen.queryByTestId("media-manager-secret"),
    ).not.toBeInTheDocument();
    expect(server.sent("POST", SECRET)).toHaveLength(1);
  });

  it("says Weir cannot be reached when the request never arrives", async () => {
    await addDeluno({
      [`POST ${SECRET}`]: () =>
        Promise.reject(new TypeError("Failed to fetch")),
    });

    fireEvent.click(screen.getByTestId("media-manager-new-secret-create"));

    expect(
      await within(screen.getByTestId(SECRET_PROMPT)).findByRole("alert"),
    ).toHaveTextContent("Can't reach Weir. Check it's still running.");
    expect(
      screen.getByTestId("media-manager-new-secret-dismiss"),
    ).toBeEnabled();
  });

  it("shows the secret once it is created after an earlier try failed", async () => {
    await addDeluno({
      [`POST ${SECRET}`]: [connectionGone, delunoSecretCreated],
    });

    fireEvent.click(screen.getByTestId("media-manager-new-secret-create"));
    await within(screen.getByTestId(SECRET_PROMPT)).findByRole("alert");
    fireEvent.click(screen.getByTestId("media-manager-new-secret-create"));

    expect(await screen.findByTestId("media-manager-secret")).toHaveTextContent(
      WEBHOOK_SECRET,
    );
    expect(
      screen.queryByText("That media manager connection does not exist."),
    ).not.toBeInTheDocument();
  });
});

describe("replacing a media manager's secret", () => {
  async function openReplaceDialog(routes: RecordedRoutes = {}) {
    const server = serve({
      [`GET ${CONNECTIONS}`]: delunoListedWithSecret,
      ...routes,
    });
    render(<MediaManagersTab />, { wrapper });
    fireEvent.click(await screen.findByTestId("media-manager-generate-secret"));
    await screen.findByTestId(REPLACE_DIALOG);
    return server;
  }

  it("leaves the working secret alone when the dialog is cancelled", async () => {
    const server = await openReplaceDialog();

    fireEvent.click(screen.getByTestId(`${REPLACE_DIALOG}-cancel`));

    expect(screen.queryByTestId(REPLACE_DIALOG)).not.toBeInTheDocument();
    expect(server.sent("POST", SECRET)).toEqual([]);
  });

  it("leaves the working secret alone when Escape closes the dialog", async () => {
    const server = await openReplaceDialog();

    fireEvent.keyDown(document, { key: "Escape" });

    expect(screen.queryByTestId(REPLACE_DIALOG)).not.toBeInTheDocument();
    expect(server.sent("POST", SECRET)).toEqual([]);
  });

  it("says why it could not be replaced and keeps the dialog open", async () => {
    const server = await openReplaceDialog({
      [`POST ${SECRET}`]: connectionGone,
    });

    fireEvent.click(screen.getByTestId(`${REPLACE_DIALOG}-confirm`));

    const dialog = screen.getByTestId(REPLACE_DIALOG);
    expect(await within(dialog).findByRole("alert")).toHaveTextContent(
      "That media manager connection does not exist.",
    );
    expect(screen.getByTestId(`${REPLACE_DIALOG}-confirm`)).toBeEnabled();
    expect(
      screen.queryByTestId("media-manager-secret"),
    ).not.toBeInTheDocument();
    expect(server.sent("POST", SECRET)).toHaveLength(1);
  });

  it("does not show an earlier failure when the dialog is opened again", async () => {
    await openReplaceDialog({ [`POST ${SECRET}`]: connectionGone });
    fireEvent.click(screen.getByTestId(`${REPLACE_DIALOG}-confirm`));
    await within(screen.getByTestId(REPLACE_DIALOG)).findByRole("alert");

    fireEvent.click(screen.getByTestId(`${REPLACE_DIALOG}-cancel`));
    fireEvent.click(screen.getByTestId("media-manager-generate-secret"));

    expect(
      within(await screen.findByTestId(REPLACE_DIALOG)).queryByRole("alert"),
    ).not.toBeInTheDocument();
  });

  it("says Replacing… and cannot be cancelled or dismissed while the new secret is made", async () => {
    const answer = held(delunoSecretCreated);
    await openReplaceDialog({ [`POST ${SECRET}`]: answer.route });

    fireEvent.click(screen.getByTestId(`${REPLACE_DIALOG}-confirm`));

    const confirm = await screen.findByRole("button", { name: "Replacing…" });
    expect(confirm).toBeDisabled();
    expect(screen.getByTestId(`${REPLACE_DIALOG}-cancel`)).toBeDisabled();
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.getByTestId(REPLACE_DIALOG)).toBeInTheDocument();
    answer.release();

    expect(await screen.findByTestId("media-manager-secret")).toHaveTextContent(
      WEBHOOK_SECRET,
    );
    expect(screen.queryByTestId(REPLACE_DIALOG)).not.toBeInTheDocument();
  });
});
