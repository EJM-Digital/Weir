import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ApiHttpError } from "../../../../lib/api/client";
import * as api from "../../../../lib/media-managers/media-managers-api";
import type {
  ConnectionInUse,
  MediaManagerConnection,
} from "../../../../lib/media-managers/media-managers-api";
import * as librariesApi from "../../../../lib/processing/libraries-api";
import { MediaManagersTab } from "./media-managers-tab";
import { stubMediaManagersTabNeighbours } from "./stub-media-managers-tab-neighbours";

function connection(): MediaManagerConnection {
  return {
    id: 7,
    kind: "deluno",
    name: "Deluno",
    enabled: true,
    base_url: "http://192.0.2.10:5099",
    api_key_is_saved: true,
    webhook_secret_is_set: false,
    webhook_url_path: "/api/v1/intake/webhook/deluno",
    downloaded_scan_enabled: false,
    unsigned_webhook_warning: null,
    last_test_ok: null,
    last_test_at: null,
    last_test_detail: null,
  };
}

function inUse(over: Partial<ConnectionInUse> = {}): ConnectionInUse {
  return {
    code: "connection_in_use",
    detail:
      "Movies and TV Shows still come from Deluno. Nothing was removed. Before removing it, unlink or remove those workflows.",
    workflows: [
      { id: 1, name: "Movies" },
      { id: 2, name: "TV Shows" },
    ],
    handoffs_in_progress: 0,
    reports: [],
    ...over,
  };
}

function owedReports() {
  return [
    { handoff_id: "h1", name: "a.mkv" },
    { handoff_id: "h2", name: "b.mkv" },
  ];
}

function wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return (
    <MemoryRouter>
      <QueryClientProvider client={qc}>{children}</QueryClientProvider>
    </MemoryRouter>
  );
}

beforeEach(() => {
  stubMediaManagersTabNeighbours();
  vi.spyOn(api, "fetchMediaManagerConnections").mockResolvedValue([
    connection(),
  ]);
});

afterEach(() => {
  vi.restoreAllMocks();
});

async function askToRemove() {
  render(<MediaManagersTab />, { wrapper });
  fireEvent.click(await screen.findByTestId("media-manager-remove"));
  fireEvent.click(screen.getByTestId("media-manager-remove-confirm-confirm"));
}

describe("a connection that workflows still come from", () => {
  it("lists the workflows and offers the two steps, and changes nothing until one is chosen", async () => {
    const del = vi
      .spyOn(api, "deleteMediaManagerConnection")
      .mockResolvedValue(inUse());
    const unlink = vi.spyOn(api, "unlinkMediaManagerConnectionWorkflows");
    const removeWorkflow = vi.spyOn(librariesApi, "deleteProcessingLibrary");

    await askToRemove();

    expect(await screen.findByTestId("media-manager-in-use")).toHaveTextContent(
      "Deluno is still needed",
    );
    expect(screen.getByTestId("media-manager-in-use-detail")).toHaveTextContent(
      "Movies and TV Shows still come from Deluno. Nothing was removed.",
    );
    expect(
      Array.from(
        screen.getByTestId("media-manager-in-use-workflows").children,
      ).map((item) => item.textContent),
    ).toEqual(["Movies", "TV Shows"]);
    expect(screen.getByTestId("media-manager-in-use-unlink")).toBeEnabled();
    expect(
      screen.getByTestId("media-manager-in-use-remove-workflows"),
    ).toBeEnabled();
    expect(screen.getByTestId("media-manager-in-use-keep")).toHaveFocus();
    expect(del).toHaveBeenCalledTimes(1);
    expect(unlink).not.toHaveBeenCalled();
    expect(removeWorkflow).not.toHaveBeenCalled();
  });

  it("leaves everything as it is when the person keeps the connection", async () => {
    const del = vi
      .spyOn(api, "deleteMediaManagerConnection")
      .mockResolvedValue(inUse());
    const unlink = vi.spyOn(api, "unlinkMediaManagerConnectionWorkflows");
    const removeWorkflow = vi.spyOn(librariesApi, "deleteProcessingLibrary");

    await askToRemove();
    fireEvent.click(await screen.findByTestId("media-manager-in-use-keep"));

    await waitFor(() =>
      expect(
        screen.queryByTestId("media-manager-in-use"),
      ).not.toBeInTheDocument(),
    );
    expect(del).toHaveBeenCalledTimes(1);
    expect(unlink).not.toHaveBeenCalled();
    expect(removeWorkflow).not.toHaveBeenCalled();
    expect(screen.getByTestId("media-manager-card")).toBeInTheDocument();
  });

  it("unlinks the workflows when chosen, says so, and asks again before removing anything", async () => {
    const del = vi
      .spyOn(api, "deleteMediaManagerConnection")
      .mockResolvedValueOnce(inUse())
      .mockResolvedValueOnce(undefined);
    const unlink = vi
      .spyOn(api, "unlinkMediaManagerConnectionWorkflows")
      .mockResolvedValue({ workflows: inUse().workflows });

    await askToRemove();
    fireEvent.click(await screen.findByTestId("media-manager-in-use-unlink"));

    const outcome = await screen.findByTestId("media-manager-remove-outcome");
    expect(outcome).toHaveTextContent(
      "Movies and TV Shows are no longer linked to Deluno.",
    );
    expect(outcome).toHaveTextContent("switched off until you choose");
    expect(unlink).toHaveBeenCalledWith(7);
    expect(
      screen.getByTestId("media-manager-remove-confirm"),
    ).toHaveTextContent("Remove Deluno?");
    expect(del).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByTestId("media-manager-remove-confirm-confirm"));

    await waitFor(() => expect(del).toHaveBeenCalledTimes(2));
    await waitFor(() =>
      expect(
        screen.queryByTestId("media-manager-remove-confirm"),
      ).not.toBeInTheDocument(),
    );
  });

  it("names a single workflow in the singular", async () => {
    vi.spyOn(api, "deleteMediaManagerConnection").mockResolvedValue(
      inUse({ workflows: [{ id: 1, name: "Movies" }] }),
    );
    vi.spyOn(api, "unlinkMediaManagerConnectionWorkflows").mockResolvedValue({
      workflows: [{ id: 1, name: "Movies" }],
    });

    await askToRemove();
    fireEvent.click(await screen.findByTestId("media-manager-in-use-unlink"));

    expect(
      await screen.findByTestId("media-manager-remove-outcome"),
    ).toHaveTextContent("Movies is no longer linked to Deluno.");
  });

  it("removes the workflows one after another when chosen, says the folders were not touched, and asks again", async () => {
    const del = vi
      .spyOn(api, "deleteMediaManagerConnection")
      .mockResolvedValue(inUse());
    const removed: number[] = [];
    vi.spyOn(librariesApi, "deleteProcessingLibrary").mockImplementation(
      async (id) => {
        removed.push(id);
      },
    );

    await askToRemove();
    fireEvent.click(
      await screen.findByTestId("media-manager-in-use-remove-workflows"),
    );

    const outcome = await screen.findByTestId("media-manager-remove-outcome");
    expect(outcome).toHaveTextContent("Movies and TV Shows were removed.");
    expect(outcome).toHaveTextContent(
      "The folders and files they watched were not touched.",
    );
    expect(removed).toEqual([1, 2]);
    expect(del).toHaveBeenCalledTimes(1);
    expect(
      screen.getByTestId("media-manager-remove-confirm"),
    ).toBeInTheDocument();
  });

  it("says why a workflow could not be removed and stops there", async () => {
    vi.spyOn(api, "deleteMediaManagerConnection").mockResolvedValue(inUse());
    const removed: number[] = [];
    vi.spyOn(librariesApi, "deleteProcessingLibrary").mockImplementation(
      async (id) => {
        if (id === 2) {
          throw new ApiHttpError(
            "/api/v1/processing/libraries/2",
            409,
            "TV Shows still has 1 job queued or running. Wait for them to finish, or cancel them, before removing the workflow.",
          );
        }
        removed.push(id);
      },
    );

    await askToRemove();
    fireEvent.click(
      await screen.findByTestId("media-manager-in-use-remove-workflows"),
    );

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "TV Shows still has 1 job queued or running.",
    );
    expect(removed).toEqual([1]);
    expect(
      screen.getByTestId("media-manager-remove-confirm"),
    ).toBeInTheDocument();
  });

  it("says why the workflows could not be unlinked", async () => {
    vi.spyOn(api, "deleteMediaManagerConnection").mockResolvedValue(inUse());
    vi.spyOn(api, "unlinkMediaManagerConnectionWorkflows").mockRejectedValue(
      new ApiHttpError(
        "/api/v1/media-managers/connections/7/unlink-workflows",
        409,
        "Weir is still working on 1 hand-off from Deluno, so its workflows stay linked. Nothing was changed. Let the hand-off finish or cancel it first.",
      ),
    );

    await askToRemove();
    fireEvent.click(await screen.findByTestId("media-manager-in-use-unlink"));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Let the hand-off finish or cancel it first.",
    );
  });

  it("offers neither step while a hand-off is in progress", async () => {
    vi.spyOn(api, "deleteMediaManagerConnection").mockResolvedValue(
      inUse({
        handoffs_in_progress: 1,
        detail:
          "Movies and TV Shows still come from Deluno. Weir is still working on 1 hand-off from it. Nothing was removed.",
      }),
    );

    await askToRemove();

    expect(
      await screen.findByTestId("media-manager-in-use-unlink"),
    ).toBeDisabled();
    expect(
      screen.getByTestId("media-manager-in-use-remove-workflows"),
    ).toBeDisabled();
    expect(screen.getByTestId("media-manager-in-use")).toHaveTextContent(
      "Neither is possible while a hand-off is in progress.",
    );
  });

  it("offers no workflow step when only hand-offs or reports are in the way", async () => {
    vi.spyOn(api, "deleteMediaManagerConnection").mockResolvedValue(
      inUse({
        workflows: [],
        reports: owedReports(),
        detail:
          "Weir still owes Deluno 2 reports on finished hand-offs. Nothing was removed. Before removing it, let Weir deliver the reports it owes or give up on them.",
      }),
    );

    await askToRemove();

    expect(
      await screen.findByTestId("media-manager-in-use-detail"),
    ).toHaveTextContent(
      "Weir still owes Deluno 2 reports on finished hand-offs.",
    );
    expect(
      screen.queryByTestId("media-manager-in-use-unlink"),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByTestId("media-manager-in-use-workflows"),
    ).not.toBeInTheDocument();
    expect(screen.getByTestId("media-manager-in-use-keep")).toBeEnabled();
  });

  it("lists the owed reports beside the workflows and offers to give up on them as its own step", async () => {
    const del = vi
      .spyOn(api, "deleteMediaManagerConnection")
      .mockResolvedValue(inUse({ reports: owedReports() }));
    const giveUp = vi.spyOn(api, "giveUpMediaManagerReports");

    await askToRemove();

    expect(
      Array.from(
        (await screen.findByTestId("media-manager-in-use-reports")).children,
      ).map((item) => item.textContent),
    ).toEqual(["a.mkv", "b.mkv"]);
    expect(
      screen.getByTestId("media-manager-in-use-workflows"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("media-manager-in-use-give-up-reports"),
    ).toHaveTextContent("Give up on the 2 reports still owed to Deluno");
    expect(del).toHaveBeenCalledTimes(1);
    expect(giveUp).not.toHaveBeenCalled();
  });

  it("gives up on the reports when chosen, says so, and asks again before removing anything", async () => {
    const del = vi
      .spyOn(api, "deleteMediaManagerConnection")
      .mockResolvedValueOnce(inUse({ workflows: [], reports: owedReports() }))
      .mockResolvedValueOnce(undefined);
    const giveUp = vi
      .spyOn(api, "giveUpMediaManagerReports")
      .mockResolvedValue({ reports: owedReports() });

    await askToRemove();
    fireEvent.click(
      await screen.findByTestId("media-manager-in-use-give-up-reports"),
    );

    const outcome = await screen.findByTestId("media-manager-remove-outcome");
    expect(outcome).toHaveTextContent(
      "Weir gave up on the reports it owed Deluno.",
    );
    expect(outcome).toHaveTextContent("Activity records them.");
    expect(giveUp).toHaveBeenCalledWith(7);
    expect(del).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByTestId("media-manager-remove-confirm-confirm"));

    await waitFor(() => expect(del).toHaveBeenCalledTimes(2));
  });

  it("names a single owed report in the singular", async () => {
    vi.spyOn(api, "deleteMediaManagerConnection").mockResolvedValue(
      inUse({ workflows: [], reports: [owedReports()[0]!] }),
    );
    vi.spyOn(api, "giveUpMediaManagerReports").mockResolvedValue({
      reports: [owedReports()[0]!],
    });

    await askToRemove();
    expect(
      await screen.findByTestId("media-manager-in-use-give-up-reports"),
    ).toHaveTextContent("Give up on the report still owed to Deluno");
    fireEvent.click(screen.getByTestId("media-manager-in-use-give-up-reports"));

    expect(
      await screen.findByTestId("media-manager-remove-outcome"),
    ).toHaveTextContent("Weir gave up on the report it owed Deluno.");
  });

  it("says why the reports could not be given up", async () => {
    vi.spyOn(api, "deleteMediaManagerConnection").mockResolvedValue(
      inUse({ workflows: [], reports: owedReports() }),
    );
    vi.spyOn(api, "giveUpMediaManagerReports").mockRejectedValue(
      new Error("Could not give up on those reports."),
    );

    await askToRemove();
    fireEvent.click(
      await screen.findByTestId("media-manager-in-use-give-up-reports"),
    );

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Could not give up on those reports.",
    );
  });
});
