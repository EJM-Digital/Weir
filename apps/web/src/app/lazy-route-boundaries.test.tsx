import { fireEvent, render, screen } from "@testing-library/react";
import {
  createMemoryRouter,
  Link,
  Outlet,
  RouterProvider,
  type RouteObject,
} from "react-router-dom";
import { describe, expect, it } from "vitest";

import { boundariesThatSurviveALazyFailure } from "./lazy-route-boundaries";

function Shell() {
  return (
    <div>
      <Link to="/page">Open the page</Link>
      <Outlet />
    </div>
  );
}

const SCREEN_ERROR = <p>Something went wrong</p>;

async function openThePage(page: RouteObject): Promise<void> {
  const router = createMemoryRouter(
    boundariesThatSurviveALazyFailure([
      {
        path: "/",
        element: <Shell />,
        errorElement: SCREEN_ERROR,
        children: [{ index: true, element: <p>Home</p> }, page],
      },
    ]),
    { initialEntries: ["/"] },
  );
  render(<RouterProvider router={router} />);
  fireEvent.click(screen.getByRole("link", { name: "Open the page" }));
}

describe("a lazy route's boundary", () => {
  it("shows the error when the route's code cannot be fetched, instead of a blank page", async () => {
    await openThePage({
      path: "page",
      errorElement: SCREEN_ERROR,
      lazy: async () => {
        throw new TypeError("Failed to fetch dynamically imported module");
      },
    });

    expect(await screen.findByText("Something went wrong")).toBeInTheDocument();
  });

  it("stays the route's own once its code has loaded, so a page that fails while it draws keeps the menu around it", async () => {
    function Broken(): never {
      throw new Error("drew badly");
    }
    await openThePage({
      path: "page",
      errorElement: SCREEN_ERROR,
      lazy: async () => ({ Component: Broken }),
    });

    expect(await screen.findByText("Something went wrong")).toBeInTheDocument();
    expect(
      screen.getByRole("link", { name: "Open the page" }),
    ).toBeInTheDocument();
  });

  it("leaves a route without code to fetch, and a lazy route without a boundary, as they were", () => {
    const plain: RouteObject = { path: "a", element: <p>A</p> };
    const lazy: RouteObject = {
      path: "b",
      lazy: async () => ({ Component: () => <p>B</p> }),
    };

    const [first, second] = boundariesThatSurviveALazyFailure([plain, lazy]);

    expect(first).toBe(plain);
    expect(second).toBe(lazy);
  });
});
