import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { ColumnsMenu } from "../../../../components/shared/columns-menu";
import type { ActiveSession } from "../../../../lib/api/types";
import { useTableColumns } from "../../../../lib/ui/use-table-columns";
import { SESSION_COLUMNS } from "./session-columns";
import { SessionsTable } from "./sessions-table";

vi.mock("../../../../lib/ui/mm-format-date", async (importOriginal) => ({
  ...(await importOriginal<
    typeof import("../../../../lib/ui/mm-format-date")
  >()),
  useAppDateFormatter: () => (iso: string) => `at ${iso.slice(0, 10)}`,
}));

function session(
  id: string,
  label: string,
  lastSeen: string,
  current = false,
): ActiveSession {
  return {
    session_id: id,
    client_label: label,
    last_seen_at: lastSeen,
    absolute_expires_at: "2026-11-01T00:00:00Z",
    created_at: "2026-09-01T00:00:00Z",
    absolute_timeout_days: 30,
    idle_timeout_minutes: 60,
    current,
    trusted_device: false,
  };
}

const SESSIONS = [
  session("a", "Firefox on Linux", "2026-10-01T00:00:00Z"),
  session("b", "Chrome on Windows", "2026-10-03T00:00:00Z", true),
  session("c", "", "2026-10-02T00:00:00Z"),
];

function Sessions() {
  const columns = useTableColumns(SESSION_COLUMNS);
  return (
    <>
      <ColumnsMenu table={columns} />
      <SessionsTable
        sessions={SESSIONS}
        columns={columns}
        revoking={false}
        onRevoke={vi.fn()}
      />
    </>
  );
}

function browsers() {
  return screen
    .getAllByRole("row")
    .slice(1)
    .map((row) => row.querySelector("th")?.textContent);
}

beforeEach(() => localStorage.clear());

describe("the active sessions table", () => {
  it("lists the browsers in the order the server gave them", () => {
    render(<Sessions />);

    expect(browsers()).toEqual([
      "Firefox on Linux",
      "Chrome on WindowsThis browser",
      "Browser session",
    ]);
  });

  it("sorts by when a browser was last seen, latest first", () => {
    render(<Sessions />);

    fireEvent.click(screen.getByRole("button", { name: "Last seen" }));

    expect(browsers()).toEqual([
      "Chrome on WindowsThis browser",
      "Browser session",
      "Firefox on Linux",
    ]);
  });

  it("sorts a browser with no name by the name it is shown under", () => {
    render(<Sessions />);

    fireEvent.click(screen.getByRole("button", { name: "Browser" }));

    expect(browsers()).toEqual([
      "Browser session",
      "Chrome on WindowsThis browser",
      "Firefox on Linux",
    ]);
  });

  it("puts the server's order back with Reset columns", () => {
    render(<Sessions />);
    fireEvent.click(screen.getByRole("button", { name: "Browser" }));

    fireEvent.click(screen.getByRole("button", { name: "Columns" }));
    fireEvent.click(screen.getByRole("menuitem", { name: "Reset columns" }));

    expect(browsers()[0]).toBe("Firefox on Linux");
  });
});

describe("a trusted device", () => {
  it("is marked Trusted beside its browser", () => {
    function Trusted() {
      const columns = useTableColumns(SESSION_COLUMNS);
      return (
        <SessionsTable
          sessions={[
            {
              ...session("t", "Safari on Mac", "2026-10-01T00:00:00Z"),
              trusted_device: true,
            },
          ]}
          columns={columns}
          revoking={false}
          onRevoke={vi.fn()}
        />
      );
    }
    render(<Trusted />);

    expect(screen.getByText("Trusted")).toBeInTheDocument();
  });
});
