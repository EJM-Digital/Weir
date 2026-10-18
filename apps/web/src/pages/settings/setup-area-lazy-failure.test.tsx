import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import { boundariesThatSurviveALazyFailure } from "../../app/lazy-route-boundaries";
import { setupRoutes } from "../../app/setup-routes";

// A tab's code is fetched when the tab is first opened, so after an update replaced the files an open page points at, one tab
// can fail to load while the rest still do. The other tabs are stood in for here; this file is about the one that fails.
vi.mock("./tabs/libraries/libraries-tab", () => ({
  LibrariesTab: () => <div>Workflows content</div>,
}));
vi.mock("./tabs/schedule/schedule-tab", () => {
  throw new TypeError("Failed to fetch dynamically imported module");
});

const SCREEN_ERROR = <p>Something went wrong</p>;

async function openSetup(entry: string) {
  const router = createMemoryRouter(
    boundariesThatSurviveALazyFailure(setupRoutes(SCREEN_ERROR)),
    { initialEntries: [entry] },
  );
  render(<RouterProvider router={router} />);
  return router;
}

afterEach(cleanup);

describe("a setup tab whose code cannot be fetched", () => {
  it("shows the error screen instead of a blank page", async () => {
    await openSetup("/setup/workflows/schedule");

    expect(await screen.findByText("Something went wrong")).toBeInTheDocument();
    expect(screen.queryByText("Workflows content")).not.toBeInTheDocument();
  });

  it("leaves the other tabs working, so going to one opens it", async () => {
    const router = await openSetup("/setup/workflows");
    expect(await screen.findByText("Workflows content")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("tab", { name: "Schedule" }));
    expect(await screen.findByText("Something went wrong")).toBeInTheDocument();

    await router.navigate("/setup/workflows");
    expect(await screen.findByText("Workflows content")).toBeInTheDocument();
    expect(screen.queryByText("Something went wrong")).not.toBeInTheDocument();
  });
});
