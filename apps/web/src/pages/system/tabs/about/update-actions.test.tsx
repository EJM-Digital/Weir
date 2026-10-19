import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type {
  UpdateStateOut,
  UpdateStatus,
} from "../../../../lib/settings/types";
import { RESTART_WAIT_MS, UpdateActions } from "./update-actions";

type Signal = { type: "changed"; topic: "update" } | { type: "restarted" };

type Step = {
  isPending: boolean;
  isError: boolean;
  isSuccess: boolean;
  error: Error | null;
  mutate: ReturnType<typeof vi.fn>;
  reset: ReturnType<typeof vi.fn>;
};

const mocks = vi.hoisted(() => ({
  useUpdateStateQuery: vi.fn(),
  useCheckUpdateMutation: vi.fn(),
  useDownloadUpdateMutation: vi.fn(),
  useApplyUpdateMutation: vi.fn(),
  subscribeLiveSignals: vi.fn(),
}));

vi.mock("../../../../lib/settings/queries", () => ({
  useUpdateStateQuery: () => mocks.useUpdateStateQuery(),
  useCheckUpdateMutation: () => mocks.useCheckUpdateMutation(),
  useDownloadUpdateMutation: () => mocks.useDownloadUpdateMutation(),
  useApplyUpdateMutation: () => mocks.useApplyUpdateMutation(),
}));

vi.mock("../../../../lib/activity/use-activity-stream-invalidation", () => ({
  subscribeLiveSignals: (subscriber: (signal: Signal) => void) =>
    mocks.subscribeLiveSignals(subscriber),
}));

const STATUS: UpdateStatus = {
  current_version: "1.0.0",
  install_type: "windows",
  in_app_upgrade_supported: true,
  status: "up_to_date",
  summary: "",
};

function step(overrides: Partial<Step> = {}): Step {
  return {
    isPending: false,
    isError: false,
    isSuccess: false,
    error: null,
    mutate: vi.fn(),
    reset: vi.fn(),
    ...overrides,
  };
}

function tray(overrides: Partial<UpdateStateOut> = {}) {
  mocks.useUpdateStateQuery.mockReturnValue({
    data: {
      downloaded: false,
      pending_version: null,
      state: "idle",
      failure: null,
      tray_running: true,
      ...overrides,
    },
  });
}

describe("UpdateActions", () => {
  let check: Step;
  let download: Step;
  let apply: Step;
  let hear: (signal: Signal) => void;
  const unsubscribe = vi.fn();

  beforeEach(() => {
    check = step();
    download = step();
    apply = step();
    mocks.useCheckUpdateMutation.mockImplementation(() => check);
    mocks.useDownloadUpdateMutation.mockImplementation(() => download);
    mocks.useApplyUpdateMutation.mockImplementation(() => apply);
    mocks.subscribeLiveSignals.mockImplementation(
      (subscriber: (signal: Signal) => void) => {
        hear = subscriber;
        return unsubscribe;
      },
    );
    tray();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    vi.useRealTimers();
    mocks.subscribeLiveSignals.mockReset();
    unsubscribe.mockReset();
  });

  const button = (name: string) => screen.getByRole("button", { name });

  it("offers Check now alone when nothing is waiting, and asks for the check when pressed", () => {
    render(<UpdateActions status={STATUS} />);

    expect(button("Check now")).toBeEnabled();
    expect(button("Download update")).toBeDisabled();
    expect(button("Restart and apply")).toBeDisabled();

    fireEvent.click(button("Check now"));

    expect(check.mutate).toHaveBeenCalledTimes(1);
    expect(download.mutate).not.toHaveBeenCalled();
  });

  it("offers the download of an update the tray found, and asks for it when pressed", () => {
    tray({ pending_version: "1.1.0" });
    render(<UpdateActions status={STATUS} />);

    fireEvent.click(button("Download update"));

    expect(download.mutate).toHaveBeenCalledTimes(1);
    expect(apply.mutate).not.toHaveBeenCalled();
  });

  it("shows the download under way in the button and in a notice, with nothing else on offer", () => {
    tray({ state: "downloading", pending_version: "1.1.0" });
    render(<UpdateActions status={STATUS} />);

    expect(button("Downloading update…")).toBeDisabled();
    expect(button("Check now")).toBeDisabled();
    expect(screen.getByRole("status")).toHaveTextContent(
      "Downloading the update — v1.1.0",
    );
  });

  it("shows the check under way in its button", () => {
    tray({ state: "checking" });
    render(<UpdateActions status={STATUS} />);

    expect(button("Checking…")).toBeDisabled();
    expect(button("Download update")).toBeDisabled();
  });

  it("says the update is ready and offers the restart, which asks for it when pressed", () => {
    tray({ state: "downloaded", downloaded: true, pending_version: "2.0.8" });
    render(<UpdateActions status={STATUS} />);

    expect(
      screen.getByText("Update ready to install — v2.0.8"),
    ).toBeInTheDocument();
    expect(button("Check now")).toBeDisabled();
    expect(button("Download update")).toBeDisabled();

    fireEvent.click(button("Restart and apply"));

    expect(apply.mutate).toHaveBeenCalledTimes(1);
  });

  it("says why a step failed, and offers the steps again", () => {
    tray({
      state: "failed",
      pending_version: "1.1.0",
      failure: "Weir could not download the update.",
    });
    render(<UpdateActions status={STATUS} />);

    expect(screen.getByRole("alert")).toHaveTextContent(
      "Weir could not download the update.",
    );
    expect(button("Check now")).toBeEnabled();
    expect(button("Download update")).toBeEnabled();
  });

  it("shows what the server refused with", () => {
    check = step({ isError: true, error: new Error("Weir is busy.") });
    render(<UpdateActions status={STATUS} />);

    expect(screen.getByRole("alert")).toHaveTextContent("Weir is busy.");
  });

  it("clears an earlier refusal when another step is asked for", () => {
    render(<UpdateActions status={STATUS} />);

    fireEvent.click(button("Check now"));

    expect(check.reset).toHaveBeenCalled();
    expect(download.reset).toHaveBeenCalled();
  });

  it("turns every button off and says why when there is no tray to answer", () => {
    tray({ tray_running: false, pending_version: "1.1.0" });
    render(
      <UpdateActions status={{ ...STATUS, status: "update_available" }} />,
    );

    expect(screen.getByRole("alert")).toHaveTextContent(
      "The Weir tray isn't running, so Weir can't update itself from here.",
    );
    for (const name of ["Check now", "Download update", "Restart and apply"]) {
      expect(button(name)).toBeDisabled();
    }
  });

  it("says a copy that was not installed with the installer cannot update itself", () => {
    const reason =
      "This copy of Weir wasn't installed with the installer, so it can't update itself. To get a newer version, download the newest Weir-win-Setup.exe from the Weir releases page on GitHub and run it.";
    tray({ tray_running: false, cannot_update_reason: reason });
    render(<UpdateActions status={STATUS} />);

    expect(screen.getByRole("alert")).toHaveTextContent(reason);
    expect(screen.getByRole("alert")).not.toHaveTextContent(
      "tray isn't running",
    );
    for (const name of ["Check now", "Download update", "Restart and apply"]) {
      expect(button(name)).toBeDisabled();
    }
  });

  describe("once the restart is signalled", () => {
    beforeEach(() => {
      tray({ state: "downloaded", downloaded: true, pending_version: "2.0.8" });
      apply = step({ isSuccess: true });
    });

    it("says the page will reload itself", () => {
      render(<UpdateActions status={STATUS} />);

      expect(
        screen.getByText(
          "Weir is restarting to finish the update. This page will reload by itself.",
        ),
      ).toBeInTheDocument();
      expect(button("Restarting…")).toBeDisabled();
    });

    it("asks the server nothing while it restarts: the stream tells the page when it is back", async () => {
      vi.useFakeTimers();
      const fetchMock = vi.fn();
      vi.stubGlobal("fetch", fetchMock);

      render(<UpdateActions status={STATUS} />);
      await vi.advanceTimersByTimeAsync(30_000);

      expect(fetchMock).not.toHaveBeenCalled();
    });

    it("goes back to offering the restart when the server came back and the update is still waiting", () => {
      render(<UpdateActions status={STATUS} />);

      act(() => hear({ type: "restarted" }));

      expect(apply.reset).toHaveBeenCalledTimes(1);
    });

    it("is not reset by a change that is not a restart", () => {
      render(<UpdateActions status={STATUS} />);

      act(() => hear({ type: "changed", topic: "update" }));

      expect(apply.reset).not.toHaveBeenCalled();
    });

    it("stops listening when it goes away", () => {
      const { unmount } = render(<UpdateActions status={STATUS} />);

      unmount();

      expect(unsubscribe).toHaveBeenCalledTimes(1);
    });

    describe("when the server does not come back", () => {
      beforeEach(() => {
        vi.useFakeTimers();
        apply.reset.mockImplementation(() => {
          apply.isSuccess = false;
        });
      });

      it("keeps saying the restart is under way for two minutes", () => {
        render(<UpdateActions status={STATUS} />);

        act(() => {
          vi.advanceTimersByTime(RESTART_WAIT_MS - 1);
        });

        expect(apply.reset).not.toHaveBeenCalled();
        expect(screen.queryByText(/has not restarted/)).toBeNull();
      });

      it("then says it has not restarted and offers the restart again", () => {
        render(<UpdateActions status={STATUS} />);

        act(() => {
          vi.advanceTimersByTime(RESTART_WAIT_MS);
        });

        expect(apply.reset).toHaveBeenCalledTimes(1);
        expect(
          screen.getByText(/Weir has not restarted after two minutes/),
        ).toBeInTheDocument();
      });

      it("takes the note away when the restart is asked for again", () => {
        render(<UpdateActions status={STATUS} />);
        act(() => {
          vi.advanceTimersByTime(RESTART_WAIT_MS);
        });

        fireEvent.click(button("Restart and apply"));

        expect(screen.queryByText(/has not restarted/)).toBeNull();
        expect(apply.mutate).toHaveBeenCalledTimes(1);
      });

      it("stops waiting when the page goes away", () => {
        const { unmount } = render(<UpdateActions status={STATUS} />);
        unmount();

        act(() => {
          vi.advanceTimersByTime(RESTART_WAIT_MS);
        });

        expect(apply.reset).not.toHaveBeenCalled();
      });
    });
  });
});
