import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { LibraryConfirmationRequired } from "../../lib/processing/library-mode-api";
import { LibraryCleanConfirm } from "./library-clean-dialog";
import { REMOVAL_IS_FINAL } from "./library-clean-model";
import type { CleanSource, LibraryClean } from "./use-library-clean";

function asked(
  overrides: Partial<LibraryConfirmationRequired> = {},
): LibraryConfirmationRequired {
  return {
    kind: "confirmation_required",
    detail: "",
    files_count: 1,
    tracks_count: 2,
    estimated_bytes_saved: 0,
    ...overrides,
  };
}

function flow(
  source: CleanSource,
  paths: string[],
  overrides: Partial<LibraryClean> & {
    asked?: LibraryConfirmationRequired;
  } = {},
) {
  const { asked: question, ...rest } = overrides;
  const cancel = vi.fn();
  const confirm = vi.fn();
  const value = {
    confirming: {
      request: { source, paths },
      asked: question ?? asked({ files_count: paths.length }),
    },
    confirmPending: false,
    confirmFailure: null,
    cancel,
    confirm,
    ...rest,
  } as unknown as LibraryClean;
  return { value, cancel, confirm };
}

describe("LibraryCleanConfirm", () => {
  it("asks nothing while there is nothing to confirm", () => {
    const { container } = render(
      <LibraryCleanConfirm
        flow={{ confirming: null } as unknown as LibraryClean}
      />,
    );

    expect(container).toBeEmptyDOMElement();
  });

  it("names a single file from its panel and offers to clean it", () => {
    const { value } = flow("file", ["/movies/A.mkv"]);
    render(<LibraryCleanConfirm flow={value} />);

    expect(
      screen.getByRole("dialog", { name: "Clean this file?" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Yes, clean it" }),
    ).toBeInTheDocument();
  });

  it("counts a selection of one file by the selection, not by the panel", () => {
    const { value } = flow("selection", ["/movies/A.mkv"]);
    render(<LibraryCleanConfirm flow={value} />);

    expect(
      screen.getByRole("dialog", { name: "Clean 1 file?" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Yes, clean it" }),
    ).toBeInTheDocument();
  });

  it("counts a selection of several files and offers to clean them", () => {
    const { value } = flow("selection", ["/movies/A.mkv", "/movies/B.mkv"]);
    render(<LibraryCleanConfirm flow={value} />);

    expect(
      screen.getByRole("dialog", { name: "Clean 2 files?" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Yes, clean them" }),
    ).toBeInTheDocument();
  });

  it("says what the clean removes and that removal is final", () => {
    const { value } = flow("selection", ["/movies/A.mkv", "/movies/B.mkv"], {
      asked: asked({ files_count: 2, tracks_count: 3 }),
    });
    render(<LibraryCleanConfirm flow={value} />);

    expect(
      screen.getByText(
        `3 tracks will come out of 2 files. ${REMOVAL_IS_FINAL}`,
      ),
    ).toBeInTheDocument();
  });

  it("lists the files Weir's checks will leave out, and only when there are some", () => {
    const quiet = flow("selection", ["/movies/A.mkv"]);
    const { unmount } = render(<LibraryCleanConfirm flow={quiet.value} />);
    expect(
      screen.queryByTestId("library-confirm-warnings"),
    ).not.toBeInTheDocument();
    unmount();

    const warned = flow("selection", ["/movies/A.mkv"], {
      asked: asked({ warnings: ["A.mkv is still seeding."] }),
    });
    render(<LibraryCleanConfirm flow={warned.value} />);

    const warnings = screen.getByTestId("library-confirm-warnings");
    expect(warnings).toHaveTextContent(
      "Weir will leave these out of the clean:",
    );
    expect(warnings).toHaveTextContent("A.mkv is still seeding.");
  });

  it("keeps things as they are on Not now and cleans only on the yes", () => {
    const { value, cancel, confirm } = flow("selection", ["/movies/A.mkv"]);
    render(<LibraryCleanConfirm flow={value} />);

    fireEvent.click(screen.getByRole("button", { name: "Not now" }));
    expect(cancel).toHaveBeenCalledTimes(1);
    expect(confirm).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Yes, clean it" }));
    expect(confirm).toHaveBeenCalledTimes(1);
  });

  it("says the clean is starting while it starts, and shows why it could not", () => {
    const { value } = flow("selection", ["/movies/A.mkv"], {
      confirmPending: true,
    });
    const { unmount } = render(<LibraryCleanConfirm flow={value} />);
    expect(
      screen.getByRole("button", { name: "Starting the clean…" }),
    ).toBeDisabled();
    unmount();

    const failed = flow("selection", ["/movies/A.mkv"], {
      confirmFailure: "Weir could not start the clean.",
    });
    render(<LibraryCleanConfirm flow={failed.value} />);
    expect(screen.getByRole("alert")).toHaveTextContent(
      "Weir could not start the clean.",
    );
  });
});
