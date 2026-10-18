import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { systemReplies } from "../../../../test/recorded-system";
import type {
  RecordedRoutes,
  RecordedWeir,
} from "../../../../test/recorded-weir";
import { CSRF_TOKEN } from "../../system-test-support";
import { renderLogList, serveLog } from "./log-wire-support";

const LOG_PATH = "/api/v1/system/log";
const CANCEL_PENDING_4 = "/api/v1/processing/jobs/4/cancel-pending";
const RECOVER_3 = "/api/v1/processing/jobs/3/recover-finalize-failed";

function renderJobs(routes: RecordedRoutes): RecordedWeir {
  const weir = serveLog(routes);
  renderLogList();
  return weir;
}

/** Opens the row at `position` in the list (newest first) and returns it. */
async function openRow(position: number): Promise<HTMLElement> {
  const rows = await screen.findAllByTestId("log-row");
  const row = rows[position].closest("li") as HTMLElement;
  fireEvent.click(within(row).getByRole("button", { expanded: false }));
  return row;
}

afterEach(() => vi.unstubAllGlobals());

describe("Cancel pending on a queued job", () => {
  it("asks the server to cancel that job, and the row then says it was cancelled", async () => {
    const weir = renderJobs({
      [`GET ${LOG_PATH}`]: [
        systemReplies.logJobsPending,
        systemReplies.logJobsAfterCancel,
      ],
      [`POST ${CANCEL_PENDING_4}`]: systemReplies.jobCancelPending,
    });
    const row = await openRow(0);

    fireEvent.click(within(row).getByTestId("processing-jobs-cancel-4"));

    await waitFor(() =>
      expect(weir.sent("POST", CANCEL_PENDING_4).map((r) => r.body)).toEqual([
        { csrf_token: CSRF_TOKEN },
      ]),
    );
    expect(
      await screen.findByText("Cancelled before it started"),
    ).toBeVisible();
    expect(
      screen.queryByTestId("processing-jobs-cancel-4"),
    ).not.toBeInTheDocument();
  });

  it("shows the server's reason when the job had already started, and keeps the button", async () => {
    renderJobs({
      [`GET ${LOG_PATH}`]: systemReplies.logJobsPending,
      [`POST ${CANCEL_PENDING_4}`]: systemReplies.jobCancelAgain,
    });
    const row = await openRow(0);

    fireEvent.click(within(row).getByTestId("processing-jobs-cancel-4"));

    expect(await within(row).findByRole("alert")).toHaveTextContent(
      "Only pending jobs can be cancelled (not leased, completed, failed, or already cancelled).",
    );
    expect(within(row).getByTestId("processing-jobs-cancel-4")).toBeEnabled();
  });

  it("says the job could not be cancelled when the server fails", async () => {
    renderJobs({
      [`GET ${LOG_PATH}`]: systemReplies.logJobsPending,
      [`POST ${CANCEL_PENDING_4}`]: systemReplies.jobCancelFails,
    });
    const row = await openRow(0);

    fireEvent.click(within(row).getByTestId("processing-jobs-cancel-4"));

    expect(await within(row).findByRole("alert")).toHaveTextContent(
      "Could not cancel job.",
    );
  });
});

describe("Recover result on a job whose work finished but whose result was not saved", () => {
  it("asks the server to recover that job, and the log is read again", async () => {
    const weir = renderJobs({
      [`GET ${LOG_PATH}`]: [
        systemReplies.logJobsFinalizeFailed,
        systemReplies.logJobsAfterRecover,
      ],
      [`POST ${RECOVER_3}`]: systemReplies.jobRecover,
    });
    const row = await openRow(2);

    fireEvent.click(within(row).getByTestId("processing-jobs-recover-3"));

    await waitFor(() =>
      expect(weir.sent("POST", RECOVER_3).map((r) => r.body)).toEqual([
        { csrf_token: CSRF_TOKEN },
      ]),
    );
    await waitFor(() => expect(weir.sent("GET", LOG_PATH)).toHaveLength(2));
    await waitFor(() =>
      expect(
        screen.queryByTestId("processing-jobs-recover-3"),
      ).not.toBeInTheDocument(),
    );
  });

  it("shows the server's reason when the job cannot be recovered", async () => {
    renderJobs({
      [`GET ${LOG_PATH}`]: systemReplies.logJobsFinalizeFailed,
      [`POST ${RECOVER_3}`]: systemReplies.jobRecoverNotFinalizeFailed,
    });
    const row = await openRow(2);

    fireEvent.click(within(row).getByTestId("processing-jobs-recover-3"));

    expect(await within(row).findByRole("alert")).toHaveTextContent(
      "Only a job whose media work completed but finalization failed can be recovered.",
    );
  });

  it("says the result could not be recovered when the server fails", async () => {
    renderJobs({
      [`GET ${LOG_PATH}`]: systemReplies.logJobsFinalizeFailed,
      [`POST ${RECOVER_3}`]: systemReplies.jobRecoverFails,
    });
    const row = await openRow(2);

    fireEvent.click(within(row).getByTestId("processing-jobs-recover-3"));

    expect(await within(row).findByRole("alert")).toHaveTextContent(
      "Could not recover that result.",
    );
  });
});
