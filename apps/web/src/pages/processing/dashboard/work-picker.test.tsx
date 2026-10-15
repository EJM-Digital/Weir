import { fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router-dom";
import { describe, expect, it } from "vitest";

import { useDashboardAddress } from "../dashboard-address";
import { DashboardControls } from "./dashboard-controls";

function Dashboard() {
  const address = useDashboardAddress([]);
  const location = useLocation();
  return (
    <>
      <DashboardControls address={address} workflows={[]} />
      <p data-testid="address">{location.search}</p>
    </>
  );
}

function renderDashboard(search: string) {
  render(
    <MemoryRouter initialEntries={[`/${search}`]}>
      <Dashboard />
    </MemoryRouter>,
  );
}

describe("the compact picker for the kind of work", () => {
  it("shows the kind of work the address names", () => {
    renderDashboard("?work=download");
    expect(screen.getByTestId("live-filter-compact")).toHaveTextContent(
      "New downloads",
    );
  });

  it("shows Everything when the address names no kind of work", () => {
    renderDashboard("");
    expect(screen.getByTestId("live-filter-compact")).toHaveTextContent(
      "Everything",
    );
  });

  it("offers the same three choices as the switch", () => {
    renderDashboard("");
    fireEvent.click(screen.getByTestId("live-filter-compact"));

    const options = within(screen.getByRole("listbox")).getAllByRole("option");
    expect(options.map((option) => option.textContent)).toEqual([
      "Everything",
      "New downloads",
      "Library cleaning",
    ]);
  });

  it("puts the choice in the address, and leaving Everything takes it out again", () => {
    renderDashboard("?view=live&workflow=3");
    fireEvent.click(screen.getByTestId("live-filter-compact"));
    fireEvent.click(screen.getByRole("option", { name: "Library cleaning" }));

    expect(screen.getByTestId("address")).toHaveTextContent(
      "?view=live&workflow=3&work=library",
    );
    expect(screen.getByTestId("live-filter-compact")).toHaveTextContent(
      "Library cleaning",
    );

    fireEvent.click(screen.getByTestId("live-filter-compact"));
    fireEvent.click(screen.getByRole("option", { name: "Everything" }));

    expect(screen.getByTestId("address")).toHaveTextContent(
      "?view=live&workflow=3",
    );
  });
});
