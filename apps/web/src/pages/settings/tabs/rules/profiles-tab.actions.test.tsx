import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import * as authQueries from "../../../../lib/auth/queries";
import * as ruleSetsApi from "../../../../lib/processing/rule-sets-api";
import { processingKeys } from "../../../../lib/processing/query-keys";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import {
  RECORDED_PROFILES,
  STORED,
  openProfiles,
  serveProfiles,
} from "../../../../test/profiles-test-support";
import { held } from "../../../../test/held";
import { ProfilesTab } from "./profiles-tab";

// The Profiles tab's Save and Remove controls and the moves between profiles: what each control looks like and says
// for a profile that is used or not, what a switch clears, and what the tab does when the list changes under it.

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

const LIST = "GET /api/v1/processing/rule-sets";
const NAME = "English feature films";

const MOVIES = RECORDED_PROFILES[0]!;
const TV = RECORDED_PROFILES[1]!;
const unused = (profile: typeof MOVIES, name = profile.name) => ({
  ...profile,
  name,
  used_by_library_count: 0,
});

const button = (name: string) => screen.getByRole("button", { name });
const nameField = () => screen.getByPlaceholderText(NAME);
const chooseProfile = (id: number) =>
  fireEvent.change(screen.getByRole("combobox", { name: "Profile" }), {
    target: { value: String(id) },
  });

/** The tab, drawn with a query client the test keeps hold of so it can have the list read again. */
function renderWithClient() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  render(<ProfilesTab />, {
    wrapper: ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    ),
  });
  return () =>
    act(() => client.invalidateQueries({ queryKey: processingKeys.ruleSets }));
}

describe("the Save and Remove buttons", () => {
  it("draws Save as the main action and Remove as a red outline", async () => {
    serveProfiles([unused(MOVIES)]);
    await openProfiles();

    expect(button("Save profile").className).toBe(
      mmActionButtonClass({ variant: "primary" }),
    );
    expect(button("Remove profile").className).toBe(
      mmActionButtonClass({ variant: "danger-outline" }),
    );
  });

  it("locks Remove for a profile a workflow uses, and says how many", async () => {
    serveProfiles([{ ...MOVIES, used_by_library_count: 1 }]);
    await openProfiles();

    expect(button("Remove profile")).toBeDisabled();
    expect(button("Remove profile")).toHaveAttribute(
      "title",
      "Detach this profile from every workflow before removing it.",
    );
    expect(
      screen.getByText("Used by 1 workflow; removal is locked."),
    ).toBeInTheDocument();
  });

  it("counts several workflows in the plural", async () => {
    serveProfiles([{ ...MOVIES, used_by_library_count: 2 }]);
    await openProfiles();

    expect(
      screen.getByText("Used by 2 workflows; removal is locked."),
    ).toBeInTheDocument();
  });

  it("offers Remove, and no lock note, for a profile nothing uses", async () => {
    serveProfiles([unused(MOVIES)]);
    await openProfiles();

    expect(button("Remove profile")).toBeEnabled();
    expect(button("Remove profile")).toHaveAttribute(
      "title",
      "Remove this unused profile.",
    );
    expect(screen.queryByText(/removal is locked/)).toBeNull();
  });

  it("keeps Remove off for a viewer even when nothing uses the profile", async () => {
    const api = serveProfiles([unused(MOVIES)]);
    api.set("GET /api/v1/auth/me", {
      user: { id: 2, role: "viewer", username: "viewer", app_theme: null },
    });
    await openProfiles();

    await waitFor(() => expect(button("Remove profile")).toBeDisabled());
    expect(button("Remove profile")).toHaveAttribute(
      "title",
      "Remove this unused profile.",
    );
  });

  it("offers no Remove while a new profile is being made", async () => {
    serveProfiles([unused(MOVIES)]);
    await openProfiles();

    fireEvent.click(button("New profile"));

    await screen.findByRole("button", { name: "Create profile" });
    expect(screen.queryByRole("button", { name: "Remove profile" })).toBeNull();
    expect(screen.queryByText(/removal is locked/)).toBeNull();
  });
});

describe("removing a profile", () => {
  /** A server with two unused profiles that, once a delete has been sent, holds back the list that no longer has it. */
  function serveRemovableProfiles() {
    const list = held<void>();
    let deleted = false;
    const api = serveProfiles([unused(MOVIES), unused(TV)]);
    api.set(LIST, async () => {
      if (!deleted) return [unused(MOVIES), unused(TV)];
      await list.promise;
      return [unused(MOVIES)];
    });
    api.set(`DELETE /api/v1/processing/rule-sets/${TV.id}`, () => {
      deleted = true;
      return new Response(null, { status: 204 });
    });
    return { api, releaseList: list.resolve };
  }

  it("names the profile in the confirmation, then says it was removed and moves to the first profile", async () => {
    const { api, releaseList } = serveRemovableProfiles();
    await openProfiles();
    chooseProfile(TV.id);
    await waitFor(() => expect(nameField()).toHaveValue(TV.name));

    fireEvent.click(button("Remove profile"));
    expect(
      screen.getByRole("dialog", { name: "Remove the profile “TV rules”?" }),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByTestId("remove-rule-set-dialog-confirm"));

    expect(await screen.findByRole("status")).toHaveTextContent(
      "TV rules was removed.",
    );
    expect(screen.queryByTestId("remove-rule-set-dialog")).toBeNull();
    // The list still has the profile until the server answers again, and the page has already moved off it.
    expect(nameField()).toHaveValue(MOVIES.name);
    expect(api.to(`DELETE /api/v1/processing/rule-sets/${TV.id}`)).toHaveLength(
      1,
    );
    releaseList();
    await waitFor(() =>
      expect(screen.queryByRole("option", { name: /TV rules/ })).toBeNull(),
    );
  });

  it("leaves the profile alone when the confirmation is cancelled", async () => {
    const { api, releaseList } = serveRemovableProfiles();
    await openProfiles();
    chooseProfile(TV.id);
    await waitFor(() => expect(nameField()).toHaveValue(TV.name));
    fireEvent.click(button("Remove profile"));

    fireEvent.click(screen.getByTestId("remove-rule-set-dialog-cancel"));

    expect(screen.queryByTestId("remove-rule-set-dialog")).toBeNull();
    expect(nameField()).toHaveValue(TV.name);
    expect(api.to(`DELETE /api/v1/processing/rule-sets/${TV.id}`)).toHaveLength(
      0,
    );
    releaseList();
  });
});

describe("saving a profile", () => {
  it("says so when the save fails without a reason", async () => {
    serveProfiles();
    vi.spyOn(ruleSetsApi, "updateProcessingRuleSet").mockRejectedValue(
      new TypeError("Failed to fetch"),
    );
    await openProfiles();

    fireEvent.click(button("Save profile"));

    expect(await screen.findByRole("status")).toHaveTextContent(
      "That profile could not be saved.",
    );
  });

  it("clears an earlier notice as soon as a save is sent", async () => {
    serveProfiles();
    const answer = held<ruleSetsApi.ProcessingRuleSet>();
    vi.spyOn(ruleSetsApi, "updateProcessingRuleSet").mockReturnValue(
      answer.promise,
    );
    await openProfiles();
    fireEvent.change(nameField(), { target: { value: "" } });
    fireEvent.click(button("Save profile"));
    expect(await screen.findByRole("status")).toHaveTextContent(
      "Give this profile a name",
    );

    fireEvent.change(nameField(), { target: { value: "Anime" } });
    fireEvent.click(button("Save profile"));

    await waitFor(() => expect(screen.queryByRole("status")).toBeNull());
    answer.resolve({ ...STORED, name: "Anime" });
    expect(await screen.findByRole("status")).toHaveTextContent(
      "Anime was saved.",
    );
  });
});

describe("moving between profiles", () => {
  it("clears a notice when another profile is chosen", async () => {
    serveProfiles();
    await openProfiles();
    fireEvent.change(nameField(), { target: { value: "" } });
    fireEvent.click(button("Save profile"));
    expect(await screen.findByRole("status")).toHaveTextContent(
      "Give this profile a name",
    );
    fireEvent.change(nameField(), { target: { value: MOVIES.name } });

    chooseProfile(TV.id);

    await waitFor(() => expect(nameField()).toHaveValue(TV.name));
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("never shows the empty state on the way from one profile to another", async () => {
    serveProfiles();
    await openProfiles();
    const remove = button("Remove profile");

    chooseProfile(TV.id);

    await waitFor(() => expect(nameField()).toHaveValue(TV.name));
    expect(screen.queryByText("No profiles yet")).toBeNull();
    // The controls stayed on screen: had the page emptied for a moment, they would have been drawn again.
    expect(remove.isConnected).toBe(true);
  });

  it("closes the track ordering editor on every switch, not only the first", async () => {
    serveProfiles();
    await openProfiles();

    for (const id of [TV.id, MOVIES.id]) {
      fireEvent.click(
        screen.getByRole("button", { name: /Advanced track ordering/ }),
      );
      expect(screen.getByText("Audio order")).toBeInTheDocument();

      chooseProfile(id);

      await waitFor(() => expect(screen.queryByText("Audio order")).toBeNull());
    }
  });

  it("closes the track ordering editor when a new profile is started, every time", async () => {
    serveProfiles([unused(MOVIES)]);
    await openProfiles();

    for (const name of ["Anime", "Documentaries"]) {
      fireEvent.click(button("New profile"));
      await screen.findByRole("button", { name: "Create profile" });
      fireEvent.click(
        screen.getByRole("button", { name: /Advanced track ordering/ }),
      );
      expect(screen.getByText("Audio order")).toBeInTheDocument();
      fireEvent.change(nameField(), { target: { value: name } });
      fireEvent.click(button("Create profile"));
      await screen.findByText(`${name} was saved.`);
      expect(screen.getByText("Audio order")).toBeInTheDocument();
    }
  });
});

describe("when the list changes under the open profile", () => {
  it("moves to the first profile when the open one is gone, and drops its edits", async () => {
    const api = serveProfiles([unused(MOVIES), unused(TV)]);
    const reread = renderWithClient();
    await screen.findByRole("heading", { name: "Audio" });
    chooseProfile(TV.id);
    await waitFor(() => expect(nameField()).toHaveValue(TV.name));
    const staleRemove = button("Remove profile");

    api.set(LIST, [unused(MOVIES)]);
    await reread();

    await waitFor(() => expect(nameField()).toHaveValue(MOVIES.name));
    // The page drew the gone profile once more without a profile behind it, and not with the other one's buttons.
    expect(staleRemove.isConnected).toBe(false);
  });

  it("says there are no profiles when the last one is gone, and starts again at the first when they return", async () => {
    const api = serveProfiles([unused(MOVIES), unused(TV)]);
    const reread = renderWithClient();
    await screen.findByRole("heading", { name: "Audio" });
    chooseProfile(TV.id);
    await waitFor(() => expect(nameField()).toHaveValue(TV.name));

    api.set(LIST, []);
    await reread();
    expect(await screen.findByText("No profiles yet")).toBeInTheDocument();
    expect(screen.queryByPlaceholderText(NAME)).toBeNull();

    api.set(LIST, [unused(MOVIES), unused(TV)]);
    await reread();

    await waitFor(() => expect(nameField()).toHaveValue(MOVIES.name));
  });
});

describe("while the tab is loading", () => {
  it("waits for the signed-in user as well as the profiles", async () => {
    const api = serveProfiles();
    vi.spyOn(authQueries, "useMeQuery").mockReturnValue({
      isPending: true,
      data: undefined,
    } as ReturnType<typeof authQueries.useMeQuery>);

    render(<ProfilesTab />, {
      wrapper: ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={new QueryClient()}>
          {children}
        </QueryClientProvider>
      ),
    });
    await waitFor(() => expect(api.to(LIST)).toHaveLength(1));
    await act(async () => {});

    expect(screen.getByText("Loading profiles")).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Audio" })).toBeNull();
  });

  it("waits for the profiles as well as the signed-in user", async () => {
    const api = serveProfiles();
    const profiles = held<void>();
    api.set(LIST, async () => {
      await profiles.promise;
      return RECORDED_PROFILES;
    });

    render(<ProfilesTab />, {
      wrapper: ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={new QueryClient()}>
          {children}
        </QueryClientProvider>
      ),
    });
    await waitFor(() => expect(api.to("GET /api/v1/auth/me")).toHaveLength(1));
    await act(async () => {});

    expect(screen.getByText("Loading profiles")).toBeInTheDocument();
    expect(screen.queryByText("No profiles yet")).toBeNull();
    profiles.resolve();
    await screen.findByRole("heading", { name: "Audio" });
  });
});
