import { render, screen } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { expect, it } from "vitest";

import { SetupAreaLayout } from "./setup-area-layout";

it("sends an address that is no setup area to the Dashboard", async () => {
  const router = createMemoryRouter(
    [
      { path: "/", element: <p>The Dashboard</p> },
      { path: "/not-a-setup-area", element: <SetupAreaLayout /> },
    ],
    { initialEntries: ["/not-a-setup-area"] },
  );

  render(<RouterProvider router={router} />);

  expect(await screen.findByText("The Dashboard")).toBeVisible();
  expect(router.state.location.pathname).toBe("/");
});
