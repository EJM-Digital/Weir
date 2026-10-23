import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { systemReplies } from "../../../../test/recorded-system";
import {
  serveRecordedReplies,
  type RecordedReply,
  type RecordedRoutes,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import { CSRF_TOKEN, SAVING } from "../../../../test/system-test-support";
import { LogClearEvents } from "./log-clear-events";

const PREVIEW = "GET /api/v1/suite/operational-history/preview";
const RESET = "POST /api/v1/suite/operational-history/reset";
const RESET_PATH = "/api/v1/suite/operational-history/reset";
const DIALOG = "logs-clear-dialog";

const onCleared = vi.fn();
const onProblem = vi.fn();

function renderClear(routes: RecordedRoutes): RecordedWeir {
  const weir = serveRecordedReplies({ ...SAVING, ...routes });
  render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <LogClearEvents onCleared={onCleared} onProblem={onProblem} />
    </QueryClientProvider>,
  );
  return weir;
}

const openDialog = async () => {
  fireEvent.click(screen.getByTestId("logs-clear-events"));
  return screen.findByTestId(DIALOG);
};
const typeReset = (word: string) =>
  fireEvent.change(screen.getByLabelText("Type RESET to confirm"), {
    target: { value: word },
  });
const confirm = () => screen.getByTestId(`${DIALOG}-confirm`);

afterEach(() => {
  vi.unstubAllGlobals();
  onCleared.mockReset();
  onProblem.mockReset();
});

describe("Clear events", () => {
  it("says what the preview counts and removes nothing until RESET is typed", async () => {
    const weir = renderClear({ [PREVIEW]: systemReplies.historyPreview });

    const dialog = await openDialog();

    expect(dialog).toHaveTextContent("3 Activity events");
    expect(dialog).toHaveTextContent("0 finished jobs");
    expect(confirm()).toBeDisabled();
    typeReset("RESET");
    expect(confirm()).toBeEnabled();
    expect(weir.sent("POST", RESET_PATH)).toEqual([]);
  });

  it("says the events could not be checked, and opens no dialog, when the preview cannot be read", async () => {
    renderClear({ [PREVIEW]: systemReplies.historyPreviewFails });

    fireEvent.click(screen.getByTestId("logs-clear-events"));

    await waitFor(() =>
      expect(onProblem).toHaveBeenLastCalledWith(
        "Could not check what clearing history would remove.",
      ),
    );
    expect(screen.queryByTestId(DIALOG)).not.toBeInTheDocument();
    expect(onCleared).not.toHaveBeenCalled();
  });

  it("clears a problem from an earlier try when a new try begins", async () => {
    renderClear({
      [PREVIEW]: [
        systemReplies.historyPreviewFails,
        systemReplies.historyPreview,
      ],
    });
    fireEvent.click(screen.getByTestId("logs-clear-events"));
    await waitFor(() => expect(onProblem).toHaveBeenCalledTimes(2));

    await openDialog();

    expect(onProblem.mock.calls).toEqual([
      [null],
      ["Could not check what clearing history would remove."],
      [null],
    ]);
  });

  it("sends the typed RESET, then says what was removed and closes", async () => {
    const weir = renderClear({
      [PREVIEW]: systemReplies.historyPreview,
      [RESET]: systemReplies.historyReset,
    });

    await openDialog();
    typeReset("reset");
    fireEvent.click(confirm());

    await waitFor(() =>
      expect(onCleared).toHaveBeenCalledWith(
        "Events cleared. Removed 3 Activity events and 0 finished jobs. No media file was touched.",
      ),
    );
    expect(weir.sent("POST", RESET_PATH).map((r) => r.body)).toEqual([
      { confirm: "reset", csrf_token: CSRF_TOKEN },
    ]);
    expect(screen.queryByTestId(DIALOG)).not.toBeInTheDocument();
  });

  it("keeps the dialog open and says inside it that the clear failed", async () => {
    renderClear({
      [PREVIEW]: systemReplies.historyPreview,
      [RESET]: systemReplies.historyResetFails,
    });

    const dialog = await openDialog();
    typeReset("RESET");
    fireEvent.click(confirm());

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Could not reset activity history.",
    );
    expect(dialog).toBeInTheDocument();
    expect(dialog).toContainElement(screen.getByRole("alert"));
    expect(confirm()).toBeEnabled();
    expect(onCleared).not.toHaveBeenCalled();
    expect(onProblem).toHaveBeenCalledTimes(1);
  });

  it("shows the server's own words when it will not clear, and drops them while the next try is out", async () => {
    let tries = 0;
    let finish: (reply: RecordedReply) => void = () => undefined;
    const secondTry = new Promise<RecordedReply>((resolve) => {
      finish = resolve;
    });
    renderClear({
      [PREVIEW]: systemReplies.historyPreview,
      [RESET]: async () =>
        tries++ === 0 ? systemReplies.historyResetWrongWord : secondTry,
    });

    await openDialog();
    typeReset("RESET");
    fireEvent.click(confirm());
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Type RESET to confirm clearing activity history.",
    );
    fireEvent.click(confirm());

    await waitFor(() =>
      expect(screen.queryByRole("alert")).not.toBeInTheDocument(),
    );
    expect(screen.getByTestId(`${DIALOG}-confirm`)).toHaveTextContent(
      "Clearing…",
    );
    finish(systemReplies.historyReset);
    await waitFor(() => expect(onCleared).toHaveBeenCalledTimes(1));
  });

  it("removes nothing when the person keeps the events", async () => {
    const weir = renderClear({ [PREVIEW]: systemReplies.historyPreview });

    await openDialog();
    typeReset("RESET");
    fireEvent.click(screen.getByTestId(`${DIALOG}-cancel`));

    expect(screen.queryByTestId(DIALOG)).not.toBeInTheDocument();
    expect(weir.sent("POST", RESET_PATH)).toEqual([]);
    expect(onCleared).not.toHaveBeenCalled();
  });
});
