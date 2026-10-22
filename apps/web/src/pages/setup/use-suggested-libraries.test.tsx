import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import * as setupApi from "../../lib/processing/library-setup-api";
import type {
  LibrarySuggestions,
  ProposedLibraryChecks,
} from "../../lib/processing/library-setup-api";
import { useSuggestedLibraries } from "./use-suggested-libraries";

const SUGGESTIONS: LibrarySuggestions = {
  libraries: [
    {
      library_id: 1,
      name: "Movies",
      media_type: "movie",
      watched_folder: "C:\\Downloads\\Movies",
      output_folder: "E:\\Ready\\Movies",
      source_label: "Deluno",
      manager_connection_ids: [5],
    },
  ],
  notes: ["Deluno keeps its movies in one folder."],
};

const CHAIN = {
  library_id: 0,
  local: { ready: true, lines: [] },
  managers: [],
  download_clients: [],
  ready: true,
};

function passing(): ProposedLibraryChecks {
  return { movie: { problem: null, chain: CHAIN } };
}

afterEach(() => vi.restoreAllMocks());

async function loaded(checks: ProposedLibraryChecks = passing()) {
  vi.spyOn(setupApi, "fetchLibrarySuggestions").mockResolvedValue(SUGGESTIONS);
  const check = vi
    .spyOn(setupApi, "checkProposedLibraries")
    .mockResolvedValue(checks);
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  const view = renderHook(() => useSuggestedLibraries(["deluno:5"]), {
    wrapper,
  });
  await waitFor(() => expect(view.result.current.rows).toHaveLength(1));
  return { view, check };
}

describe("the libraries offered from the connections", () => {
  it("offers each library ticked, with the folders and the notes the server suggested", async () => {
    const { view } = await loaded();

    expect(view.result.current.rows[0]).toMatchObject({
      name: "Movies",
      checked: true,
      watched: "C:\\Downloads\\Movies",
      output: "E:\\Ready\\Movies",
    });
    expect(view.result.current.notes).toEqual(SUGGESTIONS.notes);
    expect(view.result.current.plan).toHaveLength(1);
  });

  it("will not save a ticked library without a watched folder, and says which", async () => {
    const { view } = await loaded();

    act(() => view.result.current.edit("movie", { watched: "  " }));

    expect(view.result.current.blockedReason).toBe(
      "Add a watched folder for Movies, or untick it.",
    );
  });

  it("will not save a ticked library without an output folder, and still checks the watched folder", async () => {
    const { view, check } = await loaded();

    act(() => view.result.current.edit("movie", { output: "" }));

    expect(view.result.current.blockedReason).toBe(
      "Add an output folder for Movies: cleaned files need somewhere to go.",
    );
    await waitFor(() =>
      expect(check).toHaveBeenLastCalledWith({
        movie: { watched: "C:\\Downloads\\Movies", output: "" },
      }),
    );
  });

  it("asks for no check of a library with both folders blank", async () => {
    const { view, check } = await loaded();
    await waitFor(() => expect(check).toHaveBeenCalledTimes(1));

    act(() => view.result.current.edit("movie", { watched: "", output: "" }));

    await waitFor(() => expect(view.result.current.checking).toBe(false));
    expect(check).toHaveBeenCalledTimes(1);
  });

  it("does not hold back, or save, a library that is unticked", async () => {
    const { view } = await loaded();

    act(() =>
      view.result.current.edit("movie", { checked: false, watched: "" }),
    );

    expect(view.result.current.blockedReason).toBeNull();
    expect(view.result.current.plan).toEqual([]);
  });

  it("holds back a library whose folders the check found a problem with", async () => {
    const { view } = await loaded({
      movie: {
        problem: "Weir cannot read C:\\Downloads\\Movies.",
        chain: CHAIN,
      },
    });

    await waitFor(() =>
      expect(view.result.current.blockedReason).toBe(
        "Weir cannot read C:\\Downloads\\Movies.",
      ),
    );
  });

  it("shows no check for a library the check had nothing to say about", async () => {
    const { view, check } = await loaded({});
    await waitFor(() => expect(check).toHaveBeenCalled());

    await waitFor(() => expect(view.result.current.checking).toBe(false));
    expect(view.result.current.rows[0].check).toBeNull();
  });
});
