import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, expect, it, vi } from "vitest";

import * as processingQueries from "../../../../lib/processing/queries";
import type { SystemSettingsForm } from "../../use-system-settings-form";
import { RetentionSection } from "./retention-section";
import { useFileActivityRetention } from "./use-file-activity-retention";

const mutate = vi.fn();
const saveFrom = vi.fn();

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return (
    <MemoryRouter>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </MemoryRouter>
  );
}

function setup(
  days: number | null,
  options: { isError?: boolean; isPending?: boolean; saveError?: Error } = {},
) {
  vi.spyOn(
    processingQueries,
    "useProcessingOperatorSettingsQuery",
  ).mockReturnValue({
    data: days === null ? undefined : { file_log_retention_days: days },
    isError: options.isError ?? false,
  } as unknown as ReturnType<
    typeof processingQueries.useProcessingOperatorSettingsQuery
  >);
  vi.spyOn(
    processingQueries,
    "useProcessingOperatorSettingsSaveMutation",
  ).mockReturnValue({
    mutate,
    isPending: options.isPending ?? false,
    isError: options.saveError !== undefined,
    error: options.saveError ?? null,
  } as unknown as ReturnType<
    typeof processingQueries.useProcessingOperatorSettingsSaveMutation
  >);
}

/** The slice of the System settings form the retention card reads. */
function formWith(
  logDirty = false,
  lastSaveTarget: "logs" | "backup" | null = null,
) {
  return {
    save: { isPending: false, isError: false, error: null },
    lastSaveTarget,
    saveFrom,
    retention: {
      logValue: "30",
      activityValue: "90",
      dirty: logDirty,
      setLogDraft: vi.fn(),
      setActivityDraft: vi.fn(),
      finalizeLogDays: () => 30,
      finalizeActivityDays: () => 90,
    },
  } as unknown as SystemSettingsForm;
}

function Retention({
  editable,
  logDirty,
}: {
  editable: boolean;
  logDirty: boolean;
}) {
  return (
    <RetentionSection
      form={formWith(logDirty)}
      fileActivity={useFileActivityRetention()}
      editable={editable}
    />
  );
}

function renderRetention(editable = true, logDirty = false) {
  return render(<Retention editable={editable} logDirty={logDirty} />, {
    wrapper,
  });
}

afterEach(() => {
  vi.restoreAllMocks();
  mutate.mockReset();
  saveFrom.mockReset();
});

it("shows all three retention settings in one card, each with its days", () => {
  setup(45);

  renderRetention();

  expect(screen.getByLabelText("System log")).toHaveValue(30);
  expect(screen.getByLabelText("Events")).toHaveValue(90);
  expect(screen.getByLabelText("File activity")).toHaveValue(45);
});

it("offers one Save only once a number has changed, and saves the file activity with it", async () => {
  setup(90);
  mutate.mockImplementation(
    (_body: unknown, options: { onSuccess: () => void }) => options.onSuccess(),
  );

  renderRetention();
  expect(
    screen.queryByRole("button", { name: "Save retention" }),
  ).not.toBeInTheDocument();
  fireEvent.change(screen.getByLabelText("File activity"), {
    target: { value: "30" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Save retention" }));

  await waitFor(() =>
    expect(mutate).toHaveBeenCalledWith(
      { file_log_retention_days: 30 },
      expect.anything(),
    ),
  );
  expect(saveFrom).not.toHaveBeenCalled();
  expect(await screen.findByRole("status")).toHaveTextContent(
    "Retention saved.",
  );
});

it("saves the log and Activity numbers and the file activity together with the one button", () => {
  setup(90);

  renderRetention(true, true);
  fireEvent.change(screen.getByLabelText("File activity"), {
    target: { value: "10" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Save retention" }));

  expect(saveFrom).toHaveBeenCalledWith("logs", expect.anything());
  expect(mutate).toHaveBeenCalledWith(
    { file_log_retention_days: 10 },
    expect.anything(),
  );
});

it("refuses a file activity setting outside 0 to 3650 and a blank one", () => {
  setup(90);

  renderRetention();
  const input = screen.getByLabelText("File activity");
  fireEvent.change(input, { target: { value: "4000" } });
  expect(screen.getByRole("button", { name: "Save retention" })).toBeDisabled();
  fireEvent.change(input, { target: { value: "" } });
  expect(screen.getByRole("button", { name: "Save retention" })).toBeDisabled();
  fireEvent.change(input, { target: { value: "3650" } });
  expect(
    screen.getByRole("button", { name: "Save retention" }),
  ).not.toBeDisabled();
});

it("shows Saving… while the change is saved, and a failure in words", () => {
  setup(90, { isPending: true });
  const { unmount } = renderRetention();
  fireEvent.change(screen.getByLabelText("File activity"), {
    target: { value: "30" },
  });
  expect(screen.getByRole("button", { name: "Saving…" })).toBeDisabled();
  unmount();

  vi.restoreAllMocks();
  setup(90, { saveError: new Error("The server said no.") });
  renderRetention();
  fireEvent.change(screen.getByLabelText("File activity"), {
    target: { value: "30" },
  });
  expect(screen.getByRole("alert")).toHaveTextContent(
    /could not be saved|The server said no/,
  );
});

it("shows a viewer the numbers and no way to change them", () => {
  setup(90);

  renderRetention(false);

  expect(screen.getByLabelText("System log")).toBeDisabled();
  expect(screen.getByLabelText("Events")).toBeDisabled();
  expect(screen.getByLabelText("File activity")).toBeDisabled();
  expect(
    screen.queryByRole("button", { name: "Save retention" }),
  ).not.toBeInTheDocument();
});

it("says so when the file activity setting could not be read", () => {
  setup(null, { isError: true });

  renderRetention();

  expect(screen.getByRole("alert")).toHaveTextContent(
    "Weir could not read how long a file’s activity is kept.",
  );
});
