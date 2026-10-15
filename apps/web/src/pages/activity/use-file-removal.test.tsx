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

import { useFileRemoval } from "./use-file-removal";

const FILE = "Film (2024)/film.mkv";
const OPTIONS_URL = "/api/v1/processing/files/7/remove-options";
const FILE_URL = "/api/v1/processing/files/7";

type Answer = () => Response | Promise<Response>;

const json = (body: unknown) =>
  new Response(JSON.stringify(body), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });

/** A 200 whose body is not JSON, which the browser cannot read: the page reports it in its own words, never the browser's. */
const unreadable = () => new Response("<html>", { status: 200 });

const choiceRequired = {
  requires_choice: true,
  manager_label: null,
  delete_handled_by_manager: false,
  keep_notifies_manager: false,
  fingerprint_recorded: true,
  unconfirmed_size_bytes: null,
  unconfirmed_modified_at: null,
  delete_refused_reason: null,
};

/** Answers the page's requests by address; every call is kept so a test can say which were made. */
function server(answers: { options: Answer; forget?: Answer }) {
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    if (url.endsWith("/api/v1/auth/csrf")) {
      return Promise.resolve(json({ csrf_token: "t" }));
    }
    if (url.endsWith(OPTIONS_URL)) return Promise.resolve(answers.options());
    if (url.endsWith(FILE_URL) && init?.method === "DELETE" && answers.forget) {
      return Promise.resolve(answers.forget());
    }
    return Promise.reject(new TypeError(`unexpected request to ${url}`));
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function Harness({
  onRemoved,
  onProblem,
}: {
  onRemoved: (message: string) => void;
  onProblem: (message: string) => void;
}) {
  const removal = useFileRemoval({
    fileId: 7,
    fileName: FILE,
    onRemoved,
    onProblem,
  });
  return (
    <>
      <button type="button" onClick={() => void removal.start()}>
        Start
      </button>
      {removal.dialog}
    </>
  );
}

function start() {
  const onRemoved = vi.fn();
  const onProblem = vi.fn();
  render(
    <QueryClientProvider
      client={
        new QueryClient({
          defaultOptions: {
            queries: { retry: false },
            mutations: { retry: false },
          },
        })
      }
    >
      <Harness onRemoved={onRemoved} onProblem={onProblem} />
    </QueryClientProvider>,
  );
  fireEvent.click(screen.getByRole("button", { name: "Start" }));
  return { onRemoved, onProblem };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("removing a title from Activity", () => {
  it("says Weir could not check what removing would do when the answer cannot be read, and opens nothing", async () => {
    server({ options: unreadable });

    const { onProblem } = start();

    await waitFor(() =>
      expect(onProblem).toHaveBeenCalledWith(
        "Weir could not check what removing this file would do.",
      ),
    );
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("says the file could not be removed from the list when a plain remove cannot be read back", async () => {
    server({
      options: () => json({ ...choiceRequired, requires_choice: false }),
      forget: unreadable,
    });

    const { onProblem, onRemoved } = start();

    await waitFor(() =>
      expect(onProblem).toHaveBeenCalledWith(
        "That file could not be removed from the list.",
      ),
    );
    expect(onRemoved).not.toHaveBeenCalled();
  });

  it("shows Removing… with both buttons off while the removal runs, and Escape leaves the dialog open", async () => {
    let finish: (reply: Response) => void = () => undefined;
    const pending = new Promise<Response>((resolve) => {
      finish = resolve;
    });
    server({ options: () => json(choiceRequired), forget: () => pending });
    const { onRemoved } = start();
    const dialog = await screen.findByTestId("activity-remove-dialog");
    fireEvent.click(
      within(dialog).getByRole("radio", {
        name: /Just remove it from the list/,
      }),
    );

    fireEvent.click(screen.getByTestId("activity-remove-dialog-confirm"));

    const confirm = await screen.findByRole("button", { name: "Removing…" });
    expect(confirm).toBeDisabled();
    expect(screen.getByTestId("activity-remove-dialog-cancel")).toBeDisabled();
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.getByTestId("activity-remove-dialog")).toBeInTheDocument();

    await act(async () => {
      finish(new Response(null, { status: 204 }));
      await pending;
    });
    await waitFor(() => expect(onRemoved).toHaveBeenCalled());
    expect(
      screen.queryByTestId("activity-remove-dialog"),
    ).not.toBeInTheDocument();
  });
});
