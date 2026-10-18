import { QueryClient } from "@tanstack/react-query";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";

import { useFakeClock, useRealClock } from "../../../../test/fake-clock";
import { jsonAnswer } from "../../../../test/stub-weir-api";
import { LibrariesTab } from "./libraries-tab";
import { library } from "./library-test-fixtures";
import {
  folderChainRoute,
  managerSetupRoute,
  newTestClient,
  readyChain,
  serveWorkflows,
  signedIn,
  workflowTabWrapper,
  type WorkflowServer,
} from "./workflow-server";

// The editor here is the real one, reading from a stand-in server at `fetch`. The file is about what the editor says while
// it waits for the two checks it asks the server for (the media manager's setup and the folder chain), and when they fail.

let server: WorkflowServer;
let client: QueryClient;

beforeEach(() => {
  useFakeClock();
  client = newTestClient();
});

afterEach(() => {
  expect(server.unmatched).toEqual([]);
  useRealClock();
  vi.unstubAllGlobals();
});

async function openEditor() {
  render(<LibrariesTab />, { wrapper: workflowTabWrapper(client) });
  const row = await screen.findByTestId("processing-library-1");
  await signedIn(client);
  fireEvent.click(within(row).getByRole("button", { name: "Edit" }));
  return screen.findByTestId("processing-library-form");
}

const NEVER = () => new Promise<never>(() => undefined);

const linkedToDeluno = () =>
  serveWorkflows({
    workflows: [library({ manager_connection_ids: [5] })],
    connections: [{ id: 5, kind: "deluno", name: "Deluno" }],
  });

it("says it is reading the media managers while their setup is on its way, with the folder fields still there", async () => {
  server = linkedToDeluno();
  server.set(managerSetupRoute, NEVER);

  await openEditor();

  const setup = await screen.findByTestId("library-manager-setup");
  expect(setup).toHaveTextContent("Reading your media managers…");
  expect(screen.getByLabelText("Watched folder")).toBeInTheDocument();
  expect(screen.getByLabelText("Output folder")).toBeInTheDocument();
});

it("says Weir could not read the media managers when their setup fails, with the folder fields still there", async () => {
  server = linkedToDeluno();
  server.set(managerSetupRoute, () =>
    jsonAnswer({ detail: "Weir is not answering." }, 503),
  );

  await openEditor();

  const setup = await screen.findByTestId("library-manager-setup");
  expect(await within(setup).findByRole("alert")).toHaveTextContent(
    "Weir could not read your media managers just now. Reopen this editor in a moment.",
  );
  expect(setup).not.toHaveTextContent("Reading your media managers");
  expect(screen.getByLabelText("Watched folder")).toBeInTheDocument();
  expect(screen.getByLabelText("Output folder")).toBeInTheDocument();
});

it("says it is checking the folders while the chain is on its way", async () => {
  server = serveWorkflows({ workflows: [library()] });
  server.set(folderChainRoute(1), NEVER);

  await openEditor();

  expect(await screen.findByTestId("library-folder-chain")).toHaveTextContent(
    "Checking this workflow's folders…",
  );
});

it("checks the folders again on Check again, reading Checking… until the answer arrives", async () => {
  server = serveWorkflows({ workflows: [library()] });
  await openEditor();
  expect(await screen.findByText("Weir can read it.")).toBeInTheDocument();
  expect(server.to(folderChainRoute(1))).toHaveLength(1);

  let answer: () => void = () => undefined;
  server.set(
    folderChainRoute(1),
    () =>
      new Promise((resolve) => {
        answer = () => resolve(readyChain(1, "Weir can still read it."));
      }),
  );
  const again = screen.getByRole("button", { name: "Check again" });
  fireEvent.click(again);

  expect(
    await screen.findByRole("button", { name: "Checking…" }),
  ).toBeDisabled();
  expect(server.to(folderChainRoute(1))).toHaveLength(2);

  await act(async () => answer());
  expect(
    await screen.findByText("Weir can still read it."),
  ).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Check again" })).toBeEnabled();
});

it("says When this workflow may run is any time for a workflow with no hours drawn, with a link to Schedule", async () => {
  server = serveWorkflows({ workflows: [library()] });

  await openEditor();

  const hours = screen.getByTestId("processing-library-hours");
  expect(hours).toHaveTextContent(/^Any time\. /);
  expect(
    within(hours).getByRole("link", { name: "Change the hours in Schedule" }),
  ).toHaveAttribute("href", "/setup/workflows/schedule");
});

it("says When this workflow may run is on the hours chosen for a workflow with a week drawn", async () => {
  server = serveWorkflows({
    workflows: [library({ schedule_grid: "1".repeat(672) })],
  });

  await openEditor();

  expect(screen.getByTestId("processing-library-hours")).toHaveTextContent(
    /^On the hours chosen for it\. /,
  );
});

it("says a new workflow may run any time until hours are chosen", async () => {
  server = serveWorkflows({ workflows: [library()] });
  render(<LibrariesTab />, { wrapper: workflowTabWrapper(client) });
  fireEvent.click(await screen.findByTestId("processing-library-add"));
  fireEvent.click(await screen.findByTestId("add-workflow-continue"));
  await screen.findByTestId("processing-library-form");

  expect(screen.getByTestId("processing-library-hours")).toHaveTextContent(
    /^Any time, until you choose hours\. /,
  );
});
