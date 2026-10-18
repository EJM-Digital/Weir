import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";

import { ApiHttpError } from "../../lib/api/client";
import { ApiEntryError } from "./api-entry-error";
import { LoadError } from "./load-error";

const PATH = "/api/v1/processing/files";

afterEach(() => {
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
});

it("shows a person a plain problem and a Reload button, never the server's text", () => {
  vi.stubEnv("DEV", false);

  render(
    <ApiEntryError error={new ApiHttpError(PATH, 500, "database is locked")} />,
  );

  expect(
    screen.getByRole("heading", { name: "Weir hit a problem" }),
  ).toBeInTheDocument();
  expect(
    screen.getByText(
      "Weir couldn't load this. Reload the page. If it keeps happening, System › Logs says why.",
    ),
  ).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Reload" })).toBeInTheDocument();
  expect(document.body).not.toHaveTextContent(/database is locked|500|npm/);
});

it("loads the page again when Reload is pressed", () => {
  vi.stubEnv("DEV", false);
  const reload = vi.fn();
  vi.stubGlobal("location", { ...window.location, reload });
  render(<ApiEntryError error={new ApiHttpError(PATH, 500, "x")} />);

  fireEvent.click(screen.getByRole("button", { name: "Reload" }));

  expect(reload).toHaveBeenCalledTimes(1);
});

it("says where to look when Weir can't be reached", () => {
  vi.stubEnv("DEV", false);

  render(
    <ApiEntryError
      error={new ApiHttpError(PATH, 0, "unreachable", undefined, false, true)}
    />,
  );

  expect(
    screen.getByRole("heading", { name: "Can't reach Weir" }),
  ).toBeInTheDocument();
  expect(
    screen.getByText(
      "Make sure it's running (the tray icon on Windows, the container on Docker), then reload.",
    ),
  ).toBeInTheDocument();
});

it("keeps the developer help for vite dev", () => {
  vi.stubEnv("DEV", true);

  render(
    <ApiEntryError
      error={new ApiHttpError(PATH, 0, "unreachable", undefined, false, true)}
    />,
  );

  expect(
    screen.getByRole("heading", { name: "Cannot reach the API" }),
  ).toBeInTheDocument();
});

it("says a panel failed to load instead of looking empty", () => {
  render(
    <LoadError
      thing="these settings"
      error={new ApiHttpError(PATH, 500, "Could not load settings.")}
    />,
  );

  expect(screen.getByRole("alert")).toHaveTextContent(
    "Weir couldn't load these settings. Reload the page to try again.",
  );
});
