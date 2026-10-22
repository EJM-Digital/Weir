import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RouterProvider, createMemoryRouter } from "react-router-dom";

import { AppErrorScreen, RouteErrorScreen } from "./error-boundary";

function showRouteFailing(thrown: unknown) {
  const router = createMemoryRouter(
    [
      {
        path: "/",
        loader: () => {
          throw thrown;
        },
        element: <p>Never shown</p>,
        errorElement: <RouteErrorScreen />,
      },
    ],
    { initialEntries: ["/"] },
  );
  render(<RouterProvider router={router} />);
}

beforeEach(() => {
  vi.spyOn(console, "error").mockImplementation(() => undefined);
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("the screen shown when a route fails", () => {
  it.each([
    ["an error", new Error("Loader broke"), "Loader broke"],
    [
      "a response with a status text",
      new Response("body text", { status: 404, statusText: "Not Found" }),
      "Not Found",
    ],
    [
      "a response with only a body",
      new Response("The page is gone", { status: 410 }),
      "The page is gone",
    ],
    [
      "a response with neither",
      new Response("", { status: 500 }),
      "Route error 500",
    ],
    ["a plain message", "Plain words", "Plain words"],
    ["a blank message", "   ", "Unknown route error"],
    [
      "something that is not an error at all",
      { odd: true },
      "Unknown route error",
    ],
  ])("shows %s in the technical details", async (_what, thrown, text) => {
    showRouteFailing(thrown);

    expect(
      await screen.findByRole("heading", { name: "Something went wrong" }),
    ).toBeInTheDocument();
    expect(screen.getByText(text)).toBeInTheDocument();
  });
});

describe("the screen when an error has no message", () => {
  it("says the error is unknown", () => {
    render(<AppErrorScreen error={new Error("")} />);

    expect(screen.getByText("Unknown error")).toBeInTheDocument();
  });

  it("reloads the page from the button when the caller gave no way to", () => {
    const reload = vi.fn();
    vi.stubGlobal("location", { ...window.location, reload });
    render(<AppErrorScreen error={new Error("Broken")} />);

    fireEvent.click(screen.getByRole("button", { name: "Reload Weir" }));

    expect(reload).toHaveBeenCalledTimes(1);
  });
});
