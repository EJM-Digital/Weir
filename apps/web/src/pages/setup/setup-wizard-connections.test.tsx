import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { MediaManagerConnection } from "../../lib/media-managers/media-managers-api";
import type { ProcessingLibrary } from "../../lib/processing/libraries-api";
import { recordedBody } from "../../test/recorded";
import { adminMe } from "../../test/recorded-admin-session";
import { systemReplies } from "../../test/recorded-system";
import {
  jsonAnswer,
  stubWeirApi,
  unexpectedProblem,
  type StubbedApi,
} from "../../test/stub-weir-api";
import { held } from "../../test/held";
import { SetupWizardPage } from "./setup-wizard-page";

/**
 * The first-run wizard against a stubbed `fetch`, answering with what a running Weir server answered: its settings, its
 * workflows, and a Deluno connection whose test failed. The connections and the wizard's own steps run for real.
 */

const ME = "GET /api/v1/auth/me";
const SETTINGS = "/api/v1/suite/settings";
const MANAGERS = "/api/v1/media-managers/connections";
const SUGGESTIONS = "GET /api/v1/processing/library-suggestions";
const CSRF = "GET /api/v1/auth/csrf";

const settings = JSON.parse(systemReplies.settings.body) as object;
const emptyWorkflows = recordedBody<ProcessingLibrary[]>(
  "GET /api/v1/processing/libraries",
).map((workflow) => ({ ...workflow, watched_folder: "", output_folder: "" }));
const refusedDeluno = recordedBody<MediaManagerConnection[]>(
  `GET ${MANAGERS}`,
)[0]!;
const failedTest = recordedBody<{ ok: boolean; detail: string }>(
  `POST ${MANAGERS}/1/test`,
);
const DELUNO = `${refusedDeluno.name} · ${refusedDeluno.nickname}`;

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function serveWizard(
  extra: Record<string, unknown> = {},
  connections: MediaManagerConnection[] = [],
): StubbedApi {
  return stubWeirApi({
    [ME]: { user: JSON.parse(adminMe.body).user },
    [`GET ${SETTINGS}`]: settings,
    "GET /api/v1/processing/libraries": emptyWorkflows,
    [`GET ${MANAGERS}`]: connections,
    [SUGGESTIONS]: recordedBody("GET /api/v1/processing/library-suggestions"),
    [CSRF]: { csrf_token: "token-1" },
    ...extra,
  });
}

function renderWizard() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, retryDelay: 0 },
      mutations: { retry: false },
    },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={["/setup-wizard"]}>
        <Routes>
          <Route path="/setup-wizard" element={<SetupWizardPage />} />
          <Route path="/login" element={<p>Sign-in page</p>} />
          <Route path="/" element={<p>The app</p>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

async function chooseSource(name: string | RegExp) {
  fireEvent.click(await screen.findByRole("radio", { name }));
}

const finishButton = () =>
  screen.getByRole("button", { name: /Finish setup|Saving…/ });
const skipButton = () => screen.getByTestId("setup-wizard-skip");

describe("the wizard before it shows a form", () => {
  it("shows Loading while it asks, then sends someone who is not signed in to sign in", async () => {
    const meAnswers = held<Response>();
    serveWizard({ [ME]: () => meAnswers.promise });

    renderWizard();

    expect(await screen.findByText("Loading setup wizard")).toBeInTheDocument();
    meAnswers.resolve(jsonAnswer({ detail: "Not authenticated." }, 401));
    expect(await screen.findByText("Sign-in page")).toBeInTheDocument();
  });

  it("says setup could not be loaded when the settings cannot be read, and offers the way on", async () => {
    serveWizard({ [`GET ${SETTINGS}`]: unexpectedProblem });

    renderWizard();

    expect(
      await screen.findByRole("heading", { name: "Could not load setup" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("link", { name: "Continue to the app" }),
    ).toHaveAttribute("href", "/");
    expect(screen.queryByTestId("setup-wizard-skip")).not.toBeInTheDocument();
  });
});

describe("saving the wizard", () => {
  it("says Saving… and holds both buttons while Finish is being saved", async () => {
    const saved = held<Response>();
    const api = serveWizard({ [`PUT ${SETTINGS}`]: () => saved.promise });
    renderWizard();
    await chooseSource(/Neither/);

    fireEvent.click(finishButton());

    await waitFor(() => expect(finishButton()).toHaveTextContent("Saving…"));
    expect(finishButton()).toBeDisabled();
    expect(skipButton()).toBeDisabled();
    expect(api.to(`PUT ${SETTINGS}`)).toHaveLength(1);
    saved.resolve(jsonAnswer(settings));
    expect(
      await screen.findByText("What's next", { exact: false }),
    ).toBeVisible();
  });

  it("holds both buttons while Skip is being saved, and goes to the app once it is", async () => {
    const saved = held<Response>();
    serveWizard({ [`PUT ${SETTINGS}`]: () => saved.promise });
    renderWizard();
    await chooseSource(/Neither/);

    fireEvent.click(skipButton());

    await waitFor(() => expect(skipButton()).toBeDisabled());
    expect(finishButton()).toBeDisabled();
    saved.resolve(jsonAnswer(settings));
    expect(await screen.findByText("The app")).toBeInTheDocument();
  });
});

describe("a connection Weir already holds", () => {
  it("shows why its last test failed, and tests it again on Test, showing Testing… meanwhile", async () => {
    const tested = held<Response>();
    const api = serveWizard(
      { [`POST ${MANAGERS}/1/test`]: () => tested.promise },
      [refusedDeluno],
    );
    renderWizard();
    await chooseSource("Deluno");

    const row = within(await screen.findByTestId("setup-wizard-connection"));
    expect(row.getByRole("alert")).toHaveTextContent(
      `Weir could not connect to ${DELUNO}.`,
    );
    expect(row.getByRole("alert")).toHaveTextContent(failedTest.detail);
    fireEvent.click(row.getByRole("button", { name: "Test" }));

    expect(await row.findByText("Testing…", { selector: "p" })).toBeVisible();
    expect(row.getByRole("button", { name: "Testing…" })).toBeDisabled();
    expect(row.getByRole("button", { name: "Remove" })).toBeDisabled();
    tested.resolve(jsonAnswer(failedTest));
    await waitFor(() =>
      expect(row.getByRole("button", { name: "Test" })).toBeEnabled(),
    );
    expect(api.to(`POST ${MANAGERS}/1/test`)).toHaveLength(1);
  });

  it("says the test could not be made when the request itself fails", async () => {
    serveWizard({ [`POST ${MANAGERS}/1/test`]: unexpectedProblem }, [
      refusedDeluno,
    ]);
    renderWizard();
    await chooseSource("Deluno");
    const row = within(await screen.findByTestId("setup-wizard-connection"));

    fireEvent.click(row.getByRole("button", { name: "Test" }));

    await waitFor(() => expect(row.getAllByRole("alert")).toHaveLength(2));
  });

  it("asks before removing: Cancel keeps it, and Remove takes it out of the list", async () => {
    let remaining = [refusedDeluno];
    const api = serveWizard(
      {
        [`GET ${MANAGERS}`]: () => remaining,
        [`DELETE ${MANAGERS}/1`]: () => {
          remaining = [];
          return new Response(null, { status: 204 });
        },
      },
      remaining,
    );
    renderWizard();
    await chooseSource("Deluno");
    fireEvent.click(
      within(await screen.findByTestId("setup-wizard-connection")).getByRole(
        "button",
        { name: "Remove" },
      ),
    );

    const dialog = await screen.findByRole("dialog", {
      name: `Remove ${DELUNO}?`,
    });
    fireEvent.click(within(dialog).getByRole("button", { name: "Keep it" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(api.to(`DELETE ${MANAGERS}/1`)).toEqual([]);
    expect(screen.getByTestId("setup-wizard-connection")).toBeVisible();

    fireEvent.click(
      within(screen.getByTestId("setup-wizard-connection")).getByRole(
        "button",
        {
          name: "Remove",
        },
      ),
    );
    fireEvent.click(
      within(await screen.findByRole("dialog")).getByRole("button", {
        name: "Remove media manager",
      }),
    );

    await waitFor(() =>
      expect(
        screen.queryByTestId("setup-wizard-connection"),
      ).not.toBeInTheDocument(),
    );
    expect(api.to(`DELETE ${MANAGERS}/1`)).toHaveLength(1);
  });

  it("keeps the connection and says so when the removal fails", async () => {
    serveWizard({ [`DELETE ${MANAGERS}/1`]: unexpectedProblem }, [
      refusedDeluno,
    ]);
    renderWizard();
    await chooseSource("Deluno");
    fireEvent.click(
      within(await screen.findByTestId("setup-wizard-connection")).getByRole(
        "button",
        { name: "Remove" },
      ),
    );

    fireEvent.click(
      within(await screen.findByRole("dialog")).getByRole("button", {
        name: "Remove media manager",
      }),
    );

    expect(await screen.findByRole("alert")).toBeVisible();
    expect(screen.getByRole("dialog")).toBeVisible();
    expect(screen.getByTestId("setup-wizard-connection")).toBeVisible();
  });

  it("opens a second form on Connect another, and Cancel closes it again while the connection stays", async () => {
    serveWizard({}, [refusedDeluno]);
    renderWizard();
    await chooseSource("Deluno");
    expect(screen.queryByTestId("media-manager-save")).not.toBeInTheDocument();

    fireEvent.click(
      await screen.findByRole("button", { name: "Connect another" }),
    );

    expect(screen.getByTestId("media-manager-save")).toBeVisible();
    expect(
      screen.queryByRole("button", { name: "Connect another" }),
    ).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.queryByTestId("media-manager-save")).not.toBeInTheDocument();
    expect(screen.getByTestId("setup-wizard-connection")).toBeVisible();
    expect(
      screen.getByRole("button", { name: "Connect another" }),
    ).toBeVisible();
  });
});

describe("a source with nothing connected yet", () => {
  it("shows the form at once, and Cancel goes back to the question with nothing chosen", async () => {
    serveWizard();
    renderWizard();
    await chooseSource("Deluno");

    expect(await screen.findByTestId("media-manager-save")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    expect(screen.queryByTestId("media-manager-save")).not.toBeInTheDocument();
    for (const radio of screen.getAllByRole("radio")) {
      expect(radio).not.toBeChecked();
    }
  });

  it("offers to create a secret once the new manager has been added and answers its test", async () => {
    let connections: MediaManagerConnection[] = [];
    const created = {
      ...refusedDeluno,
      last_test_ok: null,
      last_test_at: null,
    };
    const api = serveWizard({
      [`GET ${MANAGERS}`]: () => connections,
      [`POST ${MANAGERS}`]: () => {
        connections = [created];
        return jsonAnswer(recordedBody(`POST ${MANAGERS}`), 201);
      },
      [`POST ${MANAGERS}/1/test`]: () => {
        connections = [
          {
            ...refusedDeluno,
            last_test_ok: true,
            last_test_detail: "Connected.",
          },
        ];
        return { ...failedTest, ok: true, detail: "Connected." };
      },
    });
    renderWizard();
    await chooseSource("Deluno");
    fireEvent.change(await screen.findByTestId("media-manager-base-url"), {
      target: { value: "http://localhost:5099" },
    });
    fireEvent.change(screen.getByTestId("media-manager-api-key"), {
      target: { value: "a-key" },
    });

    fireEvent.click(screen.getByTestId("media-manager-save"));

    expect(
      await screen.findByText(/^Create a secret for .* now\?$/),
    ).toBeVisible();
    expect(api.to(`POST ${MANAGERS}`)).toHaveLength(1);
    expect(api.to(`POST ${MANAGERS}/1/test`)).toHaveLength(1);
  });
});

describe("while the wizard asks what is connected", () => {
  it("says Loading while the connections are read, and that it could not when they cannot be", async () => {
    const listed = held<Response>();
    serveWizard({ [`GET ${MANAGERS}`]: () => listed.promise });
    renderWizard();
    await chooseSource("Deluno");

    expect(await screen.findByText("Loading what is connected…")).toBeVisible();
    listed.resolve(unexpectedProblem());
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Weir could not load what is connected.",
    );
  });

  it("says it is asking while the suggestions are read, and Try again asks again when they cannot be", async () => {
    const answers = held<Response>();
    let asked = 0;
    const answering = { ...refusedDeluno, last_test_ok: true };
    const api = serveWizard(
      {
        [SUGGESTIONS]: () => {
          asked += 1;
          return asked === 1
            ? answers.promise
            : recordedBody("GET /api/v1/processing/library-suggestions");
        },
      },
      [answering],
    );
    renderWizard();
    await chooseSource("Deluno");

    expect(await screen.findByText("Asking what you connected…")).toBeVisible();
    answers.resolve(unexpectedProblem());
    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent(
      "Weir could not ask what you connected for its folders just now.",
    );
    fireEvent.click(within(alert).getByRole("button", { name: "Try again" }));

    expect(await screen.findByTestId("setup-wizard-found")).toBeVisible();
    expect(api.to("GET /api/v1/processing/library-suggestions")).toHaveLength(
      2,
    );
  });
});

describe("Finish with a source chosen", () => {
  it("is refused, in words, while nothing connected answers, and saves nothing", async () => {
    const api = serveWizard({}, [refusedDeluno]);
    renderWizard();
    await chooseSource("Deluno");
    await screen.findByTestId("setup-wizard-connection");

    fireEvent.click(finishButton());

    expect(
      await screen.findByText(
        "Connect and test it first, or choose “Neither” and pick the folders yourself.",
      ),
    ).toBeVisible();
    expect(api.to(`PUT ${SETTINGS}`)).toEqual([]);
  });

  it("is refused, in words, while the folders are still being asked for", async () => {
    const asked = held<Response>();
    const api = serveWizard({ [SUGGESTIONS]: () => asked.promise }, [
      { ...refusedDeluno, last_test_ok: true },
    ]);
    renderWizard();
    await chooseSource("Deluno");
    await screen.findByText("Asking what you connected…");

    fireEvent.click(finishButton());

    expect(
      await screen.findByText(
        "Weir is still asking what you connected for its folders. Try again in a moment.",
      ),
    ).toBeVisible();
    expect(api.to(`PUT ${SETTINGS}`)).toEqual([]);
    asked.resolve(jsonAnswer({ libraries: [], notes: [] }));
  });
});
