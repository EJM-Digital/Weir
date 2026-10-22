import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, expect, it, vi } from "vitest";

import * as api from "../../lib/processing/rejected-files-api";
import { ProcessRejectedAgain } from "./activity-rejected-again";

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function renderButton(
  props: { libraryId?: number; libraryName?: string } = {},
) {
  return render(
    <ProcessRejectedAgain
      libraryId={props.libraryId}
      libraryName={props.libraryName}
    />,
    { wrapper },
  );
}

const CONFIRM = "activity-process-rejected-confirm";

function pressProcessAllAgain() {
  fireEvent.click(screen.getByRole("button", { name: "Process all again" }));
}

afterEach(() => {
  vi.restoreAllMocks();
});

it("asks how many rejected files it would process again before doing anything", async () => {
  vi.spyOn(api, "fetchRejectedFilesSummary").mockResolvedValue({
    rejected: 3,
    ready: 3,
  });
  const processAgain = vi.spyOn(api, "processRejectedFilesAgain");
  renderButton();

  pressProcessAllAgain();

  expect(
    await screen.findByRole("dialog", {
      name: "Process 3 rejected files again with your current rules?",
    }),
  ).toBeInTheDocument();
  expect(processAgain).not.toHaveBeenCalled();
});

it("says the count covers every workflow when Activity is not narrowed to one", async () => {
  vi.spyOn(api, "fetchRejectedFilesSummary").mockResolvedValue({
    rejected: 2,
    ready: 2,
  });
  renderButton();

  pressProcessAllAgain();

  expect(
    await screen.findByText(/every rejected file in all workflows/),
  ).toBeInTheDocument();
});

it("counts only the workflow Activity is narrowed to, and says which", async () => {
  const summary = vi
    .spyOn(api, "fetchRejectedFilesSummary")
    .mockResolvedValue({ rejected: 2, ready: 2 });
  renderButton({ libraryId: 4, libraryName: "Movies" });

  pressProcessAllAgain();

  expect(
    await screen.findByText(/every rejected file in the “Movies” workflow/),
  ).toBeInTheDocument();
  expect(summary).toHaveBeenCalledWith(4);
});

it("says how many rejected files it will skip because their originals are gone", async () => {
  vi.spyOn(api, "fetchRejectedFilesSummary").mockResolvedValue({
    rejected: 5,
    ready: 3,
  });
  renderButton();

  pressProcessAllAgain();

  expect(
    await screen.findByText(
      "2 other rejected files are skipped, because their originals are no longer in the watched folder.",
    ),
  ).toBeInTheDocument();
});

it("says a single skipped file in the singular", async () => {
  vi.spyOn(api, "fetchRejectedFilesSummary").mockResolvedValue({
    rejected: 2,
    ready: 1,
  });
  renderButton();

  pressProcessAllAgain();

  expect(
    await screen.findByText(
      "1 other rejected file is skipped, because its original is no longer in the watched folder.",
    ),
  ).toBeInTheDocument();
});

it("processes them again once confirmed, and counts what it queued", async () => {
  vi.spyOn(api, "fetchRejectedFilesSummary").mockResolvedValue({
    rejected: 3,
    ready: 3,
  });
  const processAgain = vi
    .spyOn(api, "processRejectedFilesAgain")
    .mockResolvedValue({
      requeued: 3,
      skipped: 0,
      detail: "Queued 3 files again. They start as capacity frees up.",
    });
  renderButton({ libraryId: 4, libraryName: "Movies" });
  pressProcessAllAgain();

  fireEvent.click(await screen.findByTestId(`${CONFIRM}-confirm`));

  expect(await screen.findByText("Queued 3 files.")).toBeInTheDocument();
  expect(processAgain).toHaveBeenCalledWith(4);
  expect(screen.queryByTestId(CONFIRM)).not.toBeInTheDocument();
});

it("does nothing when told not now", async () => {
  vi.spyOn(api, "fetchRejectedFilesSummary").mockResolvedValue({
    rejected: 3,
    ready: 3,
  });
  const processAgain = vi.spyOn(api, "processRejectedFilesAgain");
  renderButton();
  pressProcessAllAgain();

  fireEvent.click(await screen.findByTestId(`${CONFIRM}-cancel`));

  expect(screen.queryByTestId(CONFIRM)).not.toBeInTheDocument();
  expect(processAgain).not.toHaveBeenCalled();
});

it("keeps the dialog open and says so when the files could not be queued", async () => {
  vi.spyOn(api, "fetchRejectedFilesSummary").mockResolvedValue({
    rejected: 3,
    ready: 3,
  });
  vi.spyOn(api, "processRejectedFilesAgain").mockRejectedValue(
    new Error("The database is busy."),
  );
  renderButton();
  pressProcessAllAgain();

  fireEvent.click(await screen.findByTestId(`${CONFIRM}-confirm`));

  await waitFor(() =>
    expect(screen.getByRole("alert")).toHaveTextContent(
      "The database is busy.",
    ),
  );
  expect(screen.getByTestId(CONFIRM)).toBeInTheDocument();
});

it("says there is nothing to process again when no file is rejected", async () => {
  vi.spyOn(api, "fetchRejectedFilesSummary").mockResolvedValue({
    rejected: 0,
    ready: 0,
  });
  renderButton();

  pressProcessAllAgain();

  expect(await screen.findByText("No rejected files.")).toBeInTheDocument();
  expect(screen.queryByTestId(CONFIRM)).not.toBeInTheDocument();
});

it("says so, without asking, when every rejected file has lost its original", async () => {
  vi.spyOn(api, "fetchRejectedFilesSummary").mockResolvedValue({
    rejected: 4,
    ready: 0,
  });
  renderButton();

  pressProcessAllAgain();

  expect(
    await screen.findByText("Originals gone · can't process again."),
  ).toBeInTheDocument();
  expect(screen.queryByTestId(CONFIRM)).not.toBeInTheDocument();
});

it("says the one rejected file cannot be processed again when its original is gone", async () => {
  vi.spyOn(api, "fetchRejectedFilesSummary").mockResolvedValue({
    rejected: 1,
    ready: 0,
  });
  renderButton();

  pressProcessAllAgain();

  expect(
    await screen.findByText("Original gone · can't process again."),
  ).toBeInTheDocument();
});

it("says the rejected files could not be counted when the server cannot answer", async () => {
  vi.spyOn(api, "fetchRejectedFilesSummary").mockRejectedValue(
    new Error("Weir is not answering."),
  );
  renderButton();

  pressProcessAllAgain();

  expect(await screen.findByText("Weir is not answering.")).toBeInTheDocument();
  expect(screen.queryByTestId(CONFIRM)).not.toBeInTheDocument();
});

it("hands what it queued to the page instead of saying it beside the button, when the page asks", async () => {
  vi.spyOn(api, "fetchRejectedFilesSummary").mockResolvedValue({
    rejected: 1,
    ready: 1,
  });
  vi.spyOn(api, "processRejectedFilesAgain").mockResolvedValue({
    requeued: 1,
    skipped: 0,
    detail: "Queued 1 file again.",
  });
  const onNotice = vi.fn();
  render(
    <ProcessRejectedAgain
      libraryId={undefined}
      libraryName={undefined}
      onNotice={onNotice}
    />,
    { wrapper },
  );
  pressProcessAllAgain();

  fireEvent.click(await screen.findByTestId(`${CONFIRM}-confirm`));

  await waitFor(() =>
    expect(onNotice).toHaveBeenCalledWith("Queued 1 file.", false),
  );
  expect(screen.queryByRole("status")).not.toBeInTheDocument();
});

it("hands a count that failed to the page as a failure", async () => {
  vi.spyOn(api, "fetchRejectedFilesSummary").mockRejectedValue(
    new Error("Weir is not answering."),
  );
  const onNotice = vi.fn();
  render(
    <ProcessRejectedAgain
      libraryId={undefined}
      libraryName={undefined}
      onNotice={onNotice}
    />,
    { wrapper },
  );

  pressProcessAllAgain();

  await waitFor(() =>
    expect(onNotice).toHaveBeenCalledWith("Weir is not answering.", true),
  );
});

it("counts what it skipped beside what it queued", async () => {
  vi.spyOn(api, "fetchRejectedFilesSummary").mockResolvedValue({
    rejected: 4,
    ready: 3,
  });
  vi.spyOn(api, "processRejectedFilesAgain").mockResolvedValue({
    requeued: 3,
    skipped: 1,
    detail: "Queued 3 files again.",
  });
  renderButton();
  pressProcessAllAgain();

  fireEvent.click(await screen.findByTestId(`${CONFIRM}-confirm`));

  expect(
    await screen.findByText("Queued 3 files · 1 skipped."),
  ).toBeInTheDocument();
});

it("shows the server's own words when nothing was queued", async () => {
  vi.spyOn(api, "fetchRejectedFilesSummary").mockResolvedValue({
    rejected: 1,
    ready: 1,
  });
  vi.spyOn(api, "processRejectedFilesAgain").mockResolvedValue({
    requeued: 0,
    skipped: 1,
    detail: "Nothing was queued: the file is already waiting.",
  });
  renderButton();
  pressProcessAllAgain();

  fireEvent.click(await screen.findByTestId(`${CONFIRM}-confirm`));

  expect(
    await screen.findByText("Nothing was queued: the file is already waiting."),
  ).toBeInTheDocument();
});

it("says Checking, and is off, while the rejected files are counted", async () => {
  vi.spyOn(api, "fetchRejectedFilesSummary").mockReturnValue(
    new Promise(() => undefined),
  );
  renderButton();

  pressProcessAllAgain();

  expect(
    await screen.findByRole("button", { name: "Checking…" }),
  ).toBeDisabled();
});
