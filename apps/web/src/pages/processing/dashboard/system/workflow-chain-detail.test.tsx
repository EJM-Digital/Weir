import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";

import type { LibraryFolderChain } from "../../../../lib/processing/library-folder-chain-api";
import { library } from "../../../settings/tabs/libraries/library-test-fixtures";
import type { WorkflowHealth } from "../use-health";
import { WorkflowChainDrawer } from "./workflow-chain-detail";

const CHAIN: LibraryFolderChain = {
  library_id: 1,
  local: { ready: true, lines: [] },
  managers: [],
  download_clients: [],
  ready: true,
};

function health(over: Partial<WorkflowHealth>): WorkflowHealth {
  return {
    workflow: library({ id: 1, name: "Movies", manager_connection_ids: [] }),
    verdict: { words: "In sync", meaning: "done", readiness: "ready" },
    why: null,
    chain: CHAIN,
    checkedAt: null,
    recheck: async () => undefined,
    ...over,
  };
}

function open(item: WorkflowHealth) {
  render(
    <MemoryRouter>
      <WorkflowChainDrawer
        item={item}
        managers={[]}
        onClose={() => undefined}
      />
    </MemoryRouter>,
  );
}

describe("a workflow's details", () => {
  it("says the folder check is pending, and gives no reason, until the server has answered", () => {
    open(health({ chain: undefined, why: null }));

    expect(screen.getByText("Folder check pending.")).toBeInTheDocument();
    expect(document.querySelector(".mm-sy-detail__why")).toBeNull();
  });

  it("gives the reason it is not in sync beside its folder chain", () => {
    open(health({ why: "The watched folder does not exist." }));

    expect(
      screen.getByText("The watched folder does not exist."),
    ).toBeInTheDocument();
    expect(screen.queryByText("Folder check pending.")).toBeNull();
  });

  it("links to no media managers for a workflow only Weir feeds", () => {
    open(health({}));
    expect(screen.queryByRole("link", { name: "Media managers" })).toBeNull();
  });

  it("links to the media managers for a workflow a manager feeds", () => {
    open(
      health({
        workflow: library({
          id: 1,
          name: "Movies",
          manager_connection_ids: [4],
        }),
      }),
    );

    expect(
      screen.getByRole("link", { name: "Media managers" }),
    ).toBeInTheDocument();
  });
});
