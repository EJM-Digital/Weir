import {
  act,
  fireEvent,
  render,
  renderHook,
  screen,
  waitFor,
} from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { DownloadClientConnection } from "../../../lib/download-clients/download-clients-api";
import type { MediaManagerConnection } from "../../../lib/media-managers/media-managers-api";
import { CheckNowButton, useCheckNow } from "./check-now";

const testManager = vi.fn();
const testClient = vi.fn();
const recheckFolders = vi.fn();

vi.mock("../../../lib/media-managers/queries", () => ({
  useTestMediaManagerConnection: () => ({ mutateAsync: testManager }),
}));
vi.mock("../../../lib/download-clients/queries", () => ({
  useTestDownloadClientConnection: () => ({ mutateAsync: testClient }),
}));

const radarr = { id: 1, enabled: true } as MediaManagerConnection;
const sonarr = { id: 2, enabled: false } as MediaManagerConnection;
const qbittorrent = { id: 3, enabled: true } as DownloadClientConnection;

function renderCheck(
  managers: MediaManagerConnection[],
  downloadClients: DownloadClientConnection[],
) {
  return renderHook(() =>
    useCheckNow({ managers, downloadClients, recheckFolders }),
  );
}

beforeEach(() => {
  testManager.mockReset().mockResolvedValue({ ok: true });
  testClient.mockReset().mockResolvedValue({ ok: true });
  recheckFolders.mockReset().mockResolvedValue(undefined);
});

describe("Check now", () => {
  it("tests every switched-on connection and reads the folders again", async () => {
    const { result } = renderCheck([radarr, sonarr], [qbittorrent]);

    act(() => result.current.run());

    await waitFor(() => expect(result.current.pending).toBe(false));
    expect(testManager).toHaveBeenCalledTimes(1);
    expect(testManager).toHaveBeenCalledWith(1);
    expect(testClient).toHaveBeenCalledWith(3);
    expect(recheckFolders).toHaveBeenCalledTimes(1);
  });

  it("is pending while the checks run, then says all answered", async () => {
    const { result } = renderCheck([radarr], []);

    act(() => result.current.run());

    expect(result.current.pending).toBe(true);
    await waitFor(() =>
      expect(result.current.notice).toBe("Checked · all answered."),
    );
    expect(result.current.pending).toBe(false);
  });

  it("counts a connection that said no, or could not be tested, as not answering", async () => {
    testManager.mockResolvedValue({ ok: false });
    testClient.mockRejectedValue(new TypeError("offline"));
    const { result } = renderCheck([radarr], [qbittorrent]);

    act(() => result.current.run());

    await waitFor(() =>
      expect(result.current.notice).toBe("Checked · 2 didn't answer."),
    );
  });

  it("only checks the folders when no connection is switched on", async () => {
    const { result } = renderCheck([sonarr], []);

    act(() => result.current.run());

    await waitFor(() => expect(result.current.notice).toBe("Folders checked."));
    expect(testManager).not.toHaveBeenCalled();
  });
});

describe("the Check now button", () => {
  const checkNow = (pending: boolean) => ({
    pending,
    notice: null,
    run: vi.fn(),
  });

  it("offers the check, and runs it when pressed", () => {
    const check = checkNow(false);
    render(<CheckNowButton check={check} />);

    fireEvent.click(screen.getByRole("button", { name: "Check now" }));

    expect(check.run).toHaveBeenCalledTimes(1);
  });

  it("says Checking, and is off, while the checks run", () => {
    render(<CheckNowButton check={checkNow(true)} />);

    expect(screen.getByRole("button", { name: "Checking…" })).toBeDisabled();
  });
});
