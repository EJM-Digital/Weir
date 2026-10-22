import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { LogEventDetail } from "./log-event-detail";
import { LogJobDetail } from "./log-job-detail";
import { LogServerDetail } from "./log-server-detail";
import {
  EVENT_ROW,
  JOB_ROW,
  PENDING_JOB_ROW,
  SERVER_ROW,
} from "./log-test-support";

const pause = vi.hoisted(() => ({ paused: false }));

vi.mock("../../../../lib/pause/pause-queries", () => ({
  usePauseQuery: () => ({ data: { paused: pause.paused } }),
}));

beforeEach(() => {
  pause.paused = false;
});

function shown(ui: React.ReactElement) {
  const client = new QueryClient();
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>{ui}</MemoryRouter>
    </QueryClientProvider>,
  );
}

const job = JOB_ROW.job!;
const pending = PENDING_JOB_ROW.job!;

describe("an open job", () => {
  it("falls back to plain words when the job carries no message or next step", () => {
    shown(
      <LogJobDetail
        job={{ ...job, operator_message: "", next_action: "" }}
        canAct={false}
        time="2 Oct"
        onRelated={() => undefined}
        onChanged={() => undefined}
      />,
    );

    expect(screen.getByText("This job needs a review.")).toBeInTheDocument();
    expect(
      screen.getByText("Open the related screen to inspect the job."),
    ).toBeInTheDocument();
  });

  it("names a status Weir has no word for as the server sent it, and shows the worker holding the job", () => {
    shown(
      <LogJobDetail
        job={{ ...job, status: "stalled", lease_owner: "worker-2" }}
        canAct={false}
        time="2 Oct"
        onRelated={() => undefined}
        onChanged={() => undefined}
      />,
    );

    expect(screen.getByText("stalled")).toBeInTheDocument();
    expect(screen.getByText("worker-2")).toBeInTheDocument();
  });

  it("shows no input and no error section for a job that was given none and did not fail", () => {
    shown(
      <LogJobDetail
        job={{ ...job, payload_json: null, last_error: null }}
        canAct={false}
        time="2 Oct"
        onRelated={() => undefined}
        onChanged={() => undefined}
      />,
    );

    expect(screen.queryByText("What it was given")).toBeNull();
    expect(screen.queryByText("Last error")).toBeNull();
  });

  it("says a queued job is waiting while Weir is paused", () => {
    pause.paused = true;
    shown(
      <LogJobDetail
        job={pending}
        canAct
        time="2 Oct"
        onRelated={() => undefined}
        onChanged={() => undefined}
      />,
    );

    expect(screen.getByText("Waiting · Weir is paused.")).toBeInTheDocument();
    expect(
      screen.getByText("Nothing to do. Resume at the top to continue."),
    ).toBeInTheDocument();
  });

  it("offers no action on a job that is running", () => {
    shown(
      <LogJobDetail
        job={{ ...job, status: "leased" }}
        canAct
        time="2 Oct"
        onRelated={() => undefined}
        onChanged={() => undefined}
      />,
    );

    expect(screen.queryByTestId(`processing-jobs-cancel-${job.id}`)).toBeNull();
    expect(
      screen.queryByTestId(`processing-jobs-recover-${job.id}`),
    ).toBeNull();
  });
});

describe("an open server line", () => {
  const line = SERVER_ROW.server!;

  it("leaves out what the line does not say", () => {
    shown(
      <LogServerDetail
        line={{
          ...line,
          source: null,
          correlation_id: null,
          detail: null,
          traceback: null,
          job_id: null,
        }}
        time="2 Oct"
        onRelated={() => undefined}
      />,
    );

    expect(screen.queryByText("Source")).toBeNull();
    expect(screen.queryByText("Request ID")).toBeNull();
    expect(screen.queryByText("Exception")).toBeNull();
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("shows the source, the request and the detail, and narrows the log to its job", () => {
    const related = vi.fn();
    shown(
      <LogServerDetail
        line={{ ...line, source: "scanner", detail: "Took 3 seconds" }}
        time="2 Oct"
        onRelated={related}
      />,
    );

    expect(screen.getByText("scanner")).toBeInTheDocument();
    expect(screen.getByText("req-1")).toBeInTheDocument();
    expect(screen.getByText("Took 3 seconds")).toBeInTheDocument();

    fireEvent.click(
      screen.getByRole("button", { name: "Everything about this job →" }),
    );
    expect(related).toHaveBeenCalledWith(7);
  });
});

describe("an open event", () => {
  const ev = EVENT_ROW.event!;

  it("shows a result Weir has no word for as the server sent it, and none when there is none", () => {
    const { rerender } = shown(
      <LogEventDetail
        ev={{ ...ev, result: "partial" }}
        time="2 Oct"
        onRelated={() => undefined}
      />,
    );
    expect(screen.getByText("partial")).toBeInTheDocument();

    rerender(
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter>
          <LogEventDetail
            ev={{ ...ev, result: null }}
            time="2 Oct"
            onRelated={() => undefined}
          />
        </MemoryRouter>
      </QueryClientProvider>,
    );
    expect(screen.queryByText("partial")).toBeNull();
  });
});

describe("an open event that carries a processing record", () => {
  const ev = EVENT_ROW.event!;

  it("shows how far a running pass is", () => {
    shown(
      <LogEventDetail
        ev={{
          ...ev,
          event_type: "processing.file_processing_progress",
          detail: JSON.stringify({
            status: "processing",
            percent: 40,
            relative_media_path: "movies/a.mkv",
          }),
        }}
        time="2 Oct"
        onRelated={() => undefined}
      />,
    );

    expect(screen.getByText("Weir is processing a.mkv.")).toBeInTheDocument();
  });

  it("shows what a finished pass did", () => {
    shown(
      <LogEventDetail
        ev={{
          ...ev,
          event_type: "processing.file_remux_pass_completed",
          detail: JSON.stringify({
            outcome: "live_output_written",
            relative_media_path: "movies/a.mkv",
          }),
        }}
        time="2 Oct"
        onRelated={() => undefined}
      />,
    );

    expect(screen.getByText("File processed")).toBeInTheDocument();
  });
});
