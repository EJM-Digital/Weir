import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, expect, it, vi } from "vitest";

import * as api from "../../lib/processing/kept-files-api";
import { useTableColumns } from "../../lib/ui/use-table-columns";
import { KEPT_COLUMNS } from "./activity-columns";
import { ActivityKeptList } from "./activity-kept-list";

function KeptListWithColumns(
  props: Omit<Parameters<typeof ActivityKeptList>[0], "columns">,
) {
  const columns = useTableColumns(KEPT_COLUMNS);
  return <ActivityKeptList {...props} columns={columns} />;
}

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

const keptFile: api.KeptFile = {
  id: 7,
  library_id: 1,
  library_name: "Movies",
  relative_path: "Film/film.mkv",
  size_bytes: 100,
  kept_at: "2026-09-26T00:00:00",
};

afterEach(() => {
  vi.restoreAllMocks();
});

it("says nothing is kept right now when the list is empty", () => {
  render(<KeptListWithColumns files={[]} editable onProcessed={vi.fn()} />, {
    wrapper,
  });

  expect(screen.getByText("No files are kept right now.")).toBeInTheDocument();
});

it("does not offer Process again to someone who cannot edit", () => {
  render(
    <KeptListWithColumns
      files={[keptFile]}
      editable={false}
      onProcessed={vi.fn()}
    />,
    { wrapper },
  );

  expect(screen.getByTestId("kept-file-row-7")).toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Process again" }),
  ).not.toBeInTheDocument();
});

it("processing a kept file again reports the server's own message", async () => {
  const processAgain = vi.spyOn(api, "processKeptFileAgain").mockResolvedValue({
    detail:
      "Weir is checking this file's workflow now and will queue it once it is ready.",
  });
  const onProcessed = vi.fn();
  render(
    <KeptListWithColumns
      files={[keptFile]}
      editable
      onProcessed={onProcessed}
    />,
    { wrapper },
  );

  fireEvent.click(screen.getByRole("button", { name: "Process again" }));

  await waitFor(() =>
    expect(onProcessed).toHaveBeenCalledWith(
      "Weir is checking this file's workflow now and will queue it once it is ready.",
    ),
  );
  expect(processAgain).toHaveBeenCalledWith(7);
});

it("shows the server's refusal when processing a kept file again fails", async () => {
  vi.spyOn(api, "processKeptFileAgain").mockRejectedValue(
    new Error("Weir has no kept file with that id."),
  );
  render(
    <KeptListWithColumns files={[keptFile]} editable onProcessed={vi.fn()} />,
    { wrapper },
  );

  fireEvent.click(screen.getByRole("button", { name: "Process again" }));

  await waitFor(() =>
    expect(
      screen.getByText("Weir has no kept file with that id."),
    ).toBeInTheDocument(),
  );
});

it("says Queueing, and is off, while the server is asked to process a kept file again", async () => {
  vi.spyOn(api, "processKeptFileAgain").mockReturnValue(
    new Promise(() => undefined),
  );
  render(
    <KeptListWithColumns files={[keptFile]} editable onProcessed={vi.fn()} />,
    { wrapper },
  );

  fireEvent.click(screen.getByRole("button", { name: "Process again" }));

  expect(
    await screen.findByRole("button", { name: "Queueing…" }),
  ).toBeDisabled();
});
