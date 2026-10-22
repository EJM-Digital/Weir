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

async function showSetup(
  item: ProcessingManagerSetupItem,
  props: Partial<React.ComponentProps<typeof LibraryManagerSetup>> = {},
) {
  setup([item]);
  render(
    <LibraryManagerSetup
      mediaType="tv"
      watchedFolder="/media/downloads/complete"
      outputFolder="/media/downloads/weir"
      workFolder=""
      linkedConnectionIds={[item.connection_id]}
      editable
      onUseFolders={() => {}}
      {...props}
    />,
    { wrapper },
  );
  await settleFolderChecks();
  return screen.findByRole("region", { name: item.label });
}

it("says what to look up when the manager has no download client to copy a mapping from", async () => {
  const block = await showSetup({
    ...sonarr,
    mapping: { hosts: [], remote_path: "", local_path: "" },
  });

  const table = within(block).getByRole("table");
  expect(
    within(table).getByText(
      "your download client's Host, exactly as it is entered in Sonarr",
    ),
  ).toBeInTheDocument();
  expect(
    within(table).getByText("this workflow's watched folder"),
  ).toBeInTheDocument();
  expect(
    within(table).getByText("this workflow's output folder"),
  ).toBeInTheDocument();
  expect(within(block).queryByText(/more than one download client/)).toBeNull();
});

it("asks for the same mapping once for each download client", async () => {
  const block = await showSetup({
    ...sonarr,
    mapping: { ...sonarr.mapping!, hosts: ["qbittorrent", "sabnzbd"] },
  });

  expect(
    within(block).getByText("qbittorrent or sabnzbd", { selector: "code" }),
  ).toBeInTheDocument();
  expect(
    within(block).getByText(
      /Sonarr has more than one download client\. Add the same mapping once for each Host: qbittorrent or sabnzbd\./,
    ),
  ).toBeInTheDocument();
});

it("shows nothing to enter for a manager that needs no mapping", async () => {
  const block = await showSetup({ ...sonarr, mapping: null });

  expect(within(block).queryByRole("table")).not.toBeInTheDocument();
  expect(within(block).getByTestId("workflow-story")).toBeInTheDocument();
});

it("names the folders it found but offers no button to use them when the workflow cannot be edited", async () => {
  const sonarrBlock = await showSetup(
    { ...sonarr, suggested_watched_folder: "/downloads/tv-sonarr" },
    { editable: false },
  );
  expect(
    within(sonarrBlock).getByText("/downloads/tv-sonarr"),
  ).toBeInTheDocument();
  expect(
    within(sonarrBlock).queryByRole("button", {
      name: "Use this as the watched folder",
    }),
  ).not.toBeInTheDocument();
});

it("shows a Deluno that reports folders without an edit button when the workflow cannot be edited", async () => {
  const block = await showSetup(deluno, {
    editable: false,
    watchedFolder: "/media/tv",
    outputFolder: "",
  });

  expect(
    within(block).getByText(/Deluno reports downloads in/),
  ).toBeInTheDocument();
  expect(
    within(block).queryByRole("button", { name: "Use Deluno's folders" }),
  ).not.toBeInTheDocument();
});

it.each([
  {
    name: "reports no download folder",
    item: { suggested_watched_folder: undefined },
    watched: "(not set)",
    output: "/media/downloads/weir/tv",
  },
  {
    name: "reports no folder to pick cleaned files up from",
    item: { suggested_output_folder: undefined },
    watched: "/media/downloads/complete/tv",
    output: "(not set)",
  },
])("says so when Deluno $name", async ({ item, watched, output }) => {
  const block = await showSetup(
    { ...deluno, ...item },
    { watchedFolder: "/media/tv", outputFolder: "/elsewhere" },
  );

  const folders = within(block).getAllByText(
    (_text, node) => node?.tagName === "CODE",
  );
  expect(folders.map((code) => code.textContent)).toEqual([watched, output]);
});

it("says so when the media managers cannot be read, and shows none of them", async () => {
  vi.spyOn(managersApi, "fetchProcessingManagerSetup").mockRejectedValue(
    new Error("down"),
  );
  render(
    <LibraryManagerSetup
      mediaType="tv"
      watchedFolder="/in"
      outputFolder="/out"
      workFolder=""
      linkedConnectionIds={[3]}
      editable
      onUseFolders={() => {}}
    />,
    { wrapper },
  );
  await settleFolderChecks();

  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Weir could not read your media managers just now.",
  );
  expect(screen.queryByRole("region")).not.toBeInTheDocument();
});
