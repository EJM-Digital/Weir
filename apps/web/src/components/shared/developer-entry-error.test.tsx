import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { ApiHttpError } from "../../lib/api/client";
import { DeveloperEntryError } from "./developer-entry-error";

const answered = (status: number, message = "The request failed") =>
  new ApiHttpError("/api/v1/auth/bootstrap", status, message);

describe("what a developer sees when the first request fails", () => {
  it("says the API cannot be reached, and how to start it", () => {
    render(
      <DeveloperEntryError
        error={new ApiHttpError("/x", 0, "unreachable", undefined, false, true)}
      />,
    );

    expect(
      screen.getByRole("heading", { name: "Cannot reach the API" }),
    ).toBeInTheDocument();
    expect(screen.getByText("npm run dev")).toBeInTheDocument();
    expect(screen.queryByText(/not a handler bug/)).not.toBeInTheDocument();
  });

  it("explains that a 500 through the dev proxy means the API is not up", () => {
    render(<DeveloperEntryError error={answered(500)} />);

    expect(
      screen.getByRole("heading", { name: "Cannot reach the API" }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/not a handler bug inside Weir/),
    ).toBeInTheDocument();
  });

  it("says a 503 means the API is up but its database is not ready", () => {
    render(<DeveloperEntryError error={answered(503)} />);

    expect(
      screen.getByRole("heading", { name: "API is running but not ready" }),
    ).toBeInTheDocument();
    expect(screen.getByText("WEIR_HOME")).toBeInTheDocument();
  });

  it("names the status and the message of any other answer", () => {
    render(<DeveloperEntryError error={answered(404, "No such thing")} />);

    expect(
      screen.getByRole("heading", { name: "Unexpected API error" }),
    ).toBeInTheDocument();
    expect(screen.getByText(/\(HTTP 404\)/)).toBeInTheDocument();
    expect(screen.getByText("No such thing")).toBeInTheDocument();
  });

  it("gives the message of an error that did not come from the API", () => {
    render(<DeveloperEntryError error={new Error("Something odd")} />);

    expect(
      screen.getByRole("heading", { name: "Cannot load the app" }),
    ).toBeInTheDocument();
    expect(screen.getByText("Something odd")).toBeInTheDocument();
  });

  it("says no more than it knows when the failure is not even an error", () => {
    render(<DeveloperEntryError error="gone" />);

    expect(
      screen.getByRole("heading", { name: "Cannot load the app" }),
    ).toBeInTheDocument();
    expect(screen.queryByText("gone")).not.toBeInTheDocument();
  });
});
