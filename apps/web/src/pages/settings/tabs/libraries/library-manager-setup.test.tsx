import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, within } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";

import * as managersApi from "../../../../lib/processing/library-managers-api";
import type { ProcessingManagerSetupItem } from "../../../../lib/processing/library-managers-api";
import { LibraryManagerSetup } from "./library-manager-setup";
import { settleFolderChecks } from "./library-test-fixtures";
import { useFakeClock, useRealClock } from "../../../../test/fake-clock";

function wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
}

const sonarr: ProcessingManagerSetupItem = {
  connection_id: 3,
  kind: "sonarr",
  name: "Sonarr",
  label: "Sonarr",
  flow: "remote_path_mapping",
  ready: false,
  mapping: {
    hosts: ["qbittorrent"],
    remote_path: "/media/downloads/complete",
    local_path: "/media/downloads/weir",
  },
  lines: [
    { state: "ok", text: "Completed Download Handling is on in Sonarr." },
    {
      state: "problem",
      text: "Sonarr has no remote path mapping for /media/downloads/complete yet — add the one above.",
    },
  ],
};

const deluno: ProcessingManagerSetupItem = {
  connection_id: 5,
  kind: "deluno",
  name: "Deluno",
  label: "Deluno",
  flow: "handoff",
  ready: true,
  mapping: null,
  suggested_watched_folder: "/media/downloads/complete/tv",
  suggested_output_folder: "/media/downloads/weir/tv",
  lines: [
    {
      state: "unverified",
      text: "Deluno reports TV's downloads in /media/downloads/complete/tv, which isn't inside this workflow's watched folder /media/tv as Weir sees it. If Deluno has a path mapping from /media/downloads/complete to /media (Settings › Media Management › Processing Workflow › Weir › Path mappings), this is fine. Otherwise set the watched folder to /media/downloads/complete/tv.",
    },
  ],
};

function setup(managers: ProcessingManagerSetupItem[]) {
  return vi
    .spyOn(managersApi, "fetchProcessingManagerSetup")
    .mockResolvedValue({ media_type: "tv", managers });
}

// The folders settle after a debounce; the tests move the clock instead of waiting for it.
beforeEach(() => {
  useFakeClock();
});

afterEach(() => {
  useRealClock();
  vi.restoreAllMocks();
});

it("shows Sonarr exactly what to enter, with copy buttons, and leaves what is still wrong to the folder chain", async () => {
  const fetch = setup([sonarr]);
  const writeText = vi.fn().mockResolvedValue(undefined);
  Object.assign(navigator, { clipboard: { writeText } });

  render(
    <LibraryManagerSetup
      mediaType="tv"
      watchedFolder="/media/downloads/complete"
      outputFolder="/media/downloads/weir"
      workFolder=""
      linkedConnectionIds={[3]}
      editable
      onUseFolders={() => {}}
    />,
    { wrapper },
  );
  await settleFolderChecks();

  const block = await screen.findByRole("region", { name: "Sonarr" });
  expect(
    within(block).getByText(
      /Settings → Download Clients → Remote Path Mappings/,
    ),
  ).toBeInTheDocument();
  const table = within(block).getByRole("table");
  expect(within(table).getByText("qbittorrent")).toBeInTheDocument();
  expect(
    within(table).getByText("/media/downloads/complete"),
  ).toBeInTheDocument();
  expect(within(table).getByText("/media/downloads/weir")).toBeInTheDocument();
  // Whether the mapping is right is the folder chain's to say, once, in its own section.
  expect(within(block).queryByText("Needs attention")).not.toBeInTheDocument();
  expect(within(block).queryByText(/Needs a fix/)).not.toBeInTheDocument();

  fireEvent.click(
    within(block).getByRole("button", { name: "Copy Sonarr Local Path" }),
  );
  expect(writeText).toHaveBeenCalledWith("/media/downloads/weir");
  expect(await within(block).findByText("Copied")).toBeInTheDocument();
  expect(fetch).toHaveBeenCalledWith(
    "tv",
    "/media/downloads/complete",
    "/media/downloads/weir",
    true,
    [3],
  );
});

it("tells the workflow as the path a file takes, in the manager's own words", async () => {
  setup([
    {
      ...sonarr,
      story: {
        source_category: "tv-sonarr",
        manager_library: null,
        root_folder: "Z:\\TV",
      },
    },
  ]);

  render(
    <LibraryManagerSetup
      mediaType="tv"
      watchedFolder="/media/downloads/complete"
      outputFolder="/media/downloads/weir"
      workFolder="/tmp/tv"
      linkedConnectionIds={[3]}
      editable
      onUseFolders={() => {}}
    />,
    { wrapper },
  );

  expect(await screen.findByTestId("workflow-story")).toHaveTextContent(
    "Comes from Sonarr's download client, category tv-sonarr (/media/downloads/complete) → Weir works in /tmp/tv → cleaned into /media/downloads/weir → Sonarr imports it into root folder Z:\\TV.",
  );
});

it("leaves a name out of the story when the manager did not report it", async () => {
  setup([sonarr]);

  render(
    <LibraryManagerSetup
      mediaType="tv"
      watchedFolder="/media/downloads/complete"
      outputFolder="/media/downloads/weir"
      workFolder=""
      linkedConnectionIds={[3]}
      editable
      onUseFolders={() => {}}
    />,
    { wrapper },
  );

  expect(await screen.findByTestId("workflow-story")).toHaveTextContent(
    "Comes from Sonarr's download client (/media/downloads/complete) → Weir works in its own work area → cleaned into /media/downloads/weir → Sonarr imports it.",
  );
});

it("offers a Sonarr download client's own folder as a suggested watched folder", async () => {
  const onUseFolders = vi.fn();
  setup([{ ...sonarr, suggested_watched_folder: "/downloads/tv-sonarr" }]);

  render(
    <LibraryManagerSetup
      mediaType="tv"
      watchedFolder="/media/downloads/complete"
      outputFolder="/media/downloads/weir"
      workFolder=""
      linkedConnectionIds={[3]}
      editable
      onUseFolders={onUseFolders}
    />,
    { wrapper },
  );
  await settleFolderChecks();

  const block = await screen.findByRole("region", { name: "Sonarr" });
  expect(within(block).getByText("/downloads/tv-sonarr")).toBeInTheDocument();

  fireEvent.click(
    within(block).getByRole("button", {
      name: "Use this as the watched folder",
    }),
  );
  expect(onUseFolders).toHaveBeenCalledWith("/downloads/tv-sonarr", null);
});

it("does not suggest a Sonarr download client folder that is already the watched folder", async () => {
  setup([{ ...sonarr, suggested_watched_folder: "/media/downloads/complete" }]);

  render(
    <LibraryManagerSetup
      mediaType="tv"
      watchedFolder="/media/downloads/complete"
      outputFolder="/media/downloads/weir"
      workFolder=""
      linkedConnectionIds={[3]}
      editable
      onUseFolders={() => {}}
    />,
    { wrapper },
  );
  await settleFolderChecks();

  const block = await screen.findByRole("region", { name: "Sonarr" });
  expect(
    within(block).queryByRole("button", {
      name: "Use this as the watched folder",
    }),
  ).not.toBeInTheDocument();
});

it("offers a linked Deluno workflow Deluno's own folders, with no mapping table", async () => {
  setup([deluno]);
  const onUseFolders = vi.fn();

  render(
    <LibraryManagerSetup
      mediaType="tv"
      watchedFolder="/media/tv"
      outputFolder=""
      workFolder=""
      linkedConnectionIds={[5]}
      editable
      onUseFolders={onUseFolders}
    />,
    { wrapper },
  );
  await settleFolderChecks();

  const block = await screen.findByRole("region", { name: "Deluno" });
  expect(
    within(block).getByText(/Deluno reports downloads in/),
  ).toBeInTheDocument();
  expect(within(block).queryByRole("table")).not.toBeInTheDocument();
  expect(
    within(block).queryByText(/Remote Path Mappings/),
  ).not.toBeInTheDocument();

  fireEvent.click(
    within(block).getByRole("button", { name: "Use Deluno's folders" }),
  );
  expect(onUseFolders).toHaveBeenCalledWith(
    "/media/downloads/complete/tv",
    "/media/downloads/weir/tv",
  );
});

it("does not offer Deluno's folders once the workflow already uses them", async () => {
  setup([{ ...deluno, ready: true, lines: [] }]);

  render(
    <LibraryManagerSetup
      mediaType="tv"
      watchedFolder="/media/downloads/complete/tv"
      outputFolder="/media/downloads/weir/tv"
      workFolder=""
      linkedConnectionIds={[5]}
      editable
      onUseFolders={() => {}}
    />,
    { wrapper },
  );
  await settleFolderChecks();

  const block = await screen.findByRole("region", { name: "Deluno" });
  // The story already says where the files come from and go to; it is not said again.
  expect(
    within(block).queryByText(/Deluno reports downloads in/),
  ).not.toBeInTheDocument();
  expect(
    within(block).queryByRole("button", { name: "Use Deluno's folders" }),
  ).not.toBeInTheDocument();
});

it("reads nothing while the workflow is linked to no media manager", async () => {
  const fetch = setup([sonarr]);

  render(
    <LibraryManagerSetup
      mediaType="movie"
      watchedFolder="/in"
      outputFolder="/out"
      workFolder=""
      linkedConnectionIds={[]}
      editable
      onUseFolders={() => {}}
    />,
    { wrapper },
  );
  await settleFolderChecks();

  expect(
    await screen.findByTestId("library-manager-setup"),
  ).toBeInTheDocument();
  expect(fetch).not.toHaveBeenCalled();
});
