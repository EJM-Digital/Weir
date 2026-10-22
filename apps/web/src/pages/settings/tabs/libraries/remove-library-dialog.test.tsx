import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { ProcessingLibrary } from "../../../../lib/processing/libraries-api";
import type { useDeleteProcessingLibrary } from "../../../../lib/processing/libraries-queries";
import { RemoveLibraryDialog } from "./remove-library-dialog";

type Remove = ReturnType<typeof useDeleteProcessingLibrary>;

function library(watchedFolder: string): ProcessingLibrary {
  return {
    id: 7,
    name: "Movies",
    watched_folder: watchedFolder,
  } as ProcessingLibrary;
}

function removal(overrides: Partial<Remove> = {}) {
  const reset = vi.fn();
  const mutate = vi.fn();
  const value = {
    isPending: false,
    isError: false,
    error: null,
    reset,
    mutate,
    ...overrides,
  } as unknown as Remove;
  return { value, reset, mutate };
}

describe("RemoveLibraryDialog", () => {
  it("names the workflow and the folder Weir stops watching, and says nothing is undone", () => {
    const { value } = removal();
    render(
      <RemoveLibraryDialog
        library={library("  /data/movies  ")}
        remove={value}
        onClose={() => undefined}
      />,
    );

    expect(
      screen.getByRole("dialog", { name: "Remove Movies?" }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        "Weir stops watching /data/movies. Its settings and hours go with it. No media file is touched. This cannot be undone.",
      ),
    ).toBeInTheDocument();
  });

  it("says its folder when the workflow has none", () => {
    const { value } = removal();
    render(
      <RemoveLibraryDialog
        library={library("   ")}
        remove={value}
        onClose={() => undefined}
      />,
    );

    expect(
      screen.getByText(/Weir stops watching its folder\. Its settings/),
    ).toBeInTheDocument();
  });

  it("clears an earlier failure and closes on Keep it, removing nothing", () => {
    const { value, reset, mutate } = removal();
    const onClose = vi.fn();
    render(
      <RemoveLibraryDialog
        library={library("/data/movies")}
        remove={value}
        onClose={onClose}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Keep it" }));

    expect(reset).toHaveBeenCalledTimes(1);
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(mutate).not.toHaveBeenCalled();
  });

  it("removes the workflow on confirming and closes once it is removed", () => {
    const { value, mutate } = removal();
    const onClose = vi.fn();
    render(
      <RemoveLibraryDialog
        library={library("/data/movies")}
        remove={value}
        onClose={onClose}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Remove workflow" }));

    expect(mutate).toHaveBeenCalledTimes(1);
    expect(mutate.mock.calls[0]?.[0]).toBe(7);
    expect(onClose).not.toHaveBeenCalled();
    (mutate.mock.calls[0]?.[1] as { onSuccess: () => void }).onSuccess();
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("shows the server's reason when the workflow could not be removed, or a plain one when it gave none", () => {
    const reason = removal({
      isError: true,
      error: new Error("Two files are being processed in it."),
    });
    const { unmount } = render(
      <RemoveLibraryDialog
        library={library("/data/movies")}
        remove={reason.value}
        onClose={() => undefined}
      />,
    );
    expect(screen.getByRole("alert")).toHaveTextContent(
      "Two files are being processed in it.",
    );
    unmount();

    const none = removal({
      isError: true,
      error: new TypeError("x is undefined"),
    });
    render(
      <RemoveLibraryDialog
        library={library("/data/movies")}
        remove={none.value}
        onClose={() => undefined}
      />,
    );
    expect(screen.getByRole("alert")).toHaveTextContent(
      "That workflow could not be removed.",
    );
  });

  it("shows the removal as under way and stops it being asked twice", () => {
    const { value } = removal({ isPending: true });
    render(
      <RemoveLibraryDialog
        library={library("/data/movies")}
        remove={value}
        onClose={() => undefined}
      />,
    );

    expect(screen.getByRole("button", { name: "Removing…" })).toBeDisabled();
  });
});
