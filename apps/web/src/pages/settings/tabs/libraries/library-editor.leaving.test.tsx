import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { useFakeClock, useRealClock } from "../../../../test/fake-clock";
import { LibrariesTab } from "./libraries-tab";
import { LibraryEditor } from "./library-editor";
import { formFrom } from "./library-form";
import { library } from "./library-test-fixtures";
import {
  newTestClient,
  serveWorkflows,
  signedIn,
  updateRoute,
  workflowTabWrapper,
  type WorkflowServer,
} from "./workflow-server";

// The editor here is the real one, reading from a stand-in server at `fetch`. The file is about what the person can do to
// the name field and about every way of leaving the editor, since leaving it is where a typed edit is lost or kept.

let server: WorkflowServer;
let client: QueryClient;

beforeEach(() => {
  useFakeClock();
  client = newTestClient();
  server = serveWorkflows({ workflows: [library({ name: "Movies" })] });
});

afterEach(() => {
  cleanup();
  expect(server.unmatched).toEqual([]);
  useRealClock();
  vi.unstubAllGlobals();
});

async function openEditor() {
  render(<LibrariesTab />, { wrapper: workflowTabWrapper(client) });
  const row = await screen.findByTestId("processing-library-1");
  await signedIn(client);
  const edit = within(row).getByRole("button", { name: "Edit" });
  edit.focus();
  fireEvent.click(edit);
  await screen.findByTestId("processing-library-form");
  return edit;
}

const nameField = () => screen.getByLabelText("Name");
const save = () => screen.getByTestId("processing-library-save");
const asksBeforeLeaving = () =>
  screen.queryByTestId("settings-unsaved-changes");
const leave = () =>
  fireEvent.click(screen.getByTestId("settings-unsaved-changes-confirm"));
const editorIsOpen = () =>
  screen.queryByTestId("processing-library-form") !== null;

describe("the Name field", () => {
  it("keeps Save off while the name is blank or only spaces, and on once there is a name", async () => {
    await openEditor();
    expect(save()).toBeEnabled();

    fireEvent.change(nameField(), { target: { value: "" } });
    expect(save()).toBeDisabled();

    fireEvent.change(nameField(), { target: { value: "   " } });
    expect(save()).toBeDisabled();

    fireEvent.change(nameField(), { target: { value: "Films" } });
    expect(save()).toBeEnabled();
  });
});

describe("Cancel on an edited form", () => {
  it("asks first, and Leave without saving closes the editor and drops the edit", async () => {
    await openEditor();
    fireEvent.change(nameField(), { target: { value: "Films" } });

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(asksBeforeLeaving()).toHaveTextContent(
      "You have unsaved changes to Movies. Leave without saving?",
    );
    leave();

    expect(editorIsOpen()).toBe(false);
    expect(screen.getByTestId("processing-library-1")).toHaveTextContent(
      "Movies",
    );
    expect(server.to(updateRoute(1))).toHaveLength(0);

    fireEvent.click(screen.getByRole("button", { name: "Edit" }));
    expect(await screen.findByLabelText("Name")).toHaveValue("Movies");
  });
});

describe("closing an edited editor any other way", () => {
  const ways: [string, () => void][] = [
    [
      "the × in its corner",
      () =>
        fireEvent.click(
          within(screen.getByRole("dialog")).getByRole("button", {
            name: "Close",
          }),
        ),
    ],
    [
      "a click on the page behind it",
      () =>
        fireEvent.click(
          screen
            .getAllByRole("button", { name: "Close" })
            .find((button) => button.classList.contains("mm-drawer-backdrop"))!,
        ),
    ],
    ["Escape", () => fireEvent.keyDown(document, { key: "Escape" })],
  ];

  it.each(ways)(
    "asks first for %s, and keeps the edit when told to stay",
    async (_, close) => {
      await openEditor();
      fireEvent.change(nameField(), { target: { value: "Films" } });

      close();

      expect(asksBeforeLeaving()).toHaveTextContent("Movies");
      expect(editorIsOpen()).toBe(true);
      fireEvent.click(screen.getByTestId("settings-unsaved-changes-cancel"));
      expect(asksBeforeLeaving()).not.toBeInTheDocument();
      expect(nameField()).toHaveValue("Films");
    },
  );

  it.each(ways)(
    "closes after %s once the person leaves, and focus goes back to the Edit button",
    async (_, close) => {
      const edit = await openEditor();
      fireEvent.change(nameField(), { target: { value: "Films" } });

      close();
      leave();

      await waitFor(() => expect(editorIsOpen()).toBe(false));
      expect(edit).toHaveFocus();
      expect(server.to(updateRoute(1))).toHaveLength(0);
    },
  );

  it.each(ways)(
    "does not ask for %s when nothing was edited",
    async (_, close) => {
      await openEditor();

      close();

      expect(asksBeforeLeaving()).not.toBeInTheDocument();
      expect(editorIsOpen()).toBe(false);
    },
  );
});

describe("the editor opened by a viewer", () => {
  it("has every field and Save switched off", async () => {
    server = serveWorkflows({
      workflows: [library({ name: "Movies" })],
      role: "viewer",
    });
    const saved = server.workflows[0]!;
    render(
      <MemoryRouter>
        <QueryClientProvider client={client}>
          <LibraryEditor
            library={saved}
            initial={formFrom(saved)}
            editable={false}
            ruleSets={[]}
            connections={[]}
            onSave={() => Promise.resolve()}
            onClose={() => undefined}
          />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    const form = await screen.findByTestId("processing-library-form");
    const fields = form.querySelectorAll("input, select, textarea");
    expect(fields.length).toBeGreaterThan(10);
    for (const field of fields) {
      expect(
        field,
        field.getAttribute("aria-label") ?? field.outerHTML,
      ).toBeDisabled();
    }
    expect(screen.getByTestId("processing-library-save")).toBeDisabled();
  });
});
