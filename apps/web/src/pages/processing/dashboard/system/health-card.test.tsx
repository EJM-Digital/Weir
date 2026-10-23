import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ProcessingLibrary } from "../../../../lib/processing/libraries-api";
import type { CheckNow } from "../check-now";
import type { Health } from "../use-health";
import { HealthCard } from "./health-card";
import type { StatusMeaning } from "../../../../lib/ui/status-meaning";
import type { HealthArea, HealthCheck } from "./health-checks";
import type { HealthChecks } from "./use-health-checks";
import { SHEEN_MS } from "./use-sheen";

const NOW = Date.parse("2026-10-02T12:00:00Z");

const movies = {
  id: 2,
  name: "Movies",
  output_folder: "D:\\Movies",
  minimum_free_disk_space_mb: 20480,
  manager_connection_ids: [],
} as unknown as ProcessingLibrary;

const health: Health = {
  workflows: [
    {
      workflow: movies,
      verdict: {
        words: "Needs a fix",
        meaning: "attention",
        readiness: "needs_attention",
      },
      why: "The output folder is missing.",
      chain: undefined,
      checkedAt: NOW - 12_000,
      recheck: async () => undefined,
    },
  ],
  managers: [],
  downloadClients: [],
  tools: null,
  foldersReadAt: null,
  recheckFolders: vi.fn(),
};
const checks: HealthChecks = {
  checks: [],
  checking: false,
  overviewChecks: null,
  health,
  readAt: null,
  busy: new Set(),
  again: vi.fn(async () => undefined),
};
const check: CheckNow = { run: vi.fn(), pending: false, notice: null };
let fits = Number.MAX_SAFE_INTEGER;

vi.mock("../fit-rows", () => ({
  useFittingRows: () => [{ current: null }, fits],
}));
vi.mock("./use-health-checks", () => ({ useHealthChecks: () => checks }));
vi.mock("../check-now", () => ({
  useCheckNow: () => check,
  CheckNowButton: ({ check: current }: { check: CheckNow }) => (
    <button type="button" onClick={current.run}>
      Check now
    </button>
  ),
}));

function item(
  id: string,
  area: HealthArea,
  meaning: StatusMeaning,
  overrides: Partial<HealthCheck> = {},
): HealthCheck {
  return {
    id,
    area,
    meaning,
    title: id,
    why: `${id} why`,
    words: `${id} words`,
    checkedAt: NOW - 12_000,
    fix: null,
    again: { area, key: null },
    workflowId: null,
    ...overrides,
  };
}

function Card({ jump }: { jump?: number }) {
  return (
    <MemoryRouter>
      <HealthCard workflows={[movies]} jump={jump} />
    </MemoryRouter>
  );
}

function renderCard(jump?: number) {
  const view = render(<Card jump={jump} />);
  return {
    ...view,
    card: screen.getByRole("region", { name: "Health" }),
  };
}

const RADARR = "Radarr isn't answering";

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(NOW);
  fits = Number.MAX_SAFE_INTEGER;
  checks.busy = new Set();
  checks.checking = false;
  checks.overviewChecks = null;
  checks.checks = [
    item("movies", "workflows", "attention", {
      title: "Movies needs a fix",
      why: "The output folder D:/Movies does not exist. Create it, or point this workflow at one that does.",
      words: "Output folder missing · create it",
      fix: {
        label: "Fix it →",
        to: "/setup/workflows?edit=2",
        note: "Opens it.",
      },
      again: { area: "workflows", key: "2" },
      workflowId: 2,
    }),
    item("tv", "workflows", "done", { title: "TV", workflowId: 3 }),
    item("radarr", "connections", "broken", { title: RADARR }),
    item("ffmpeg", "tools", "done", { title: "FFmpeg" }),
  ];
  vi.mocked(checks.again).mockClear();
});

afterEach(() => {
  vi.useRealTimers();
});

describe("the Health card", () => {
  it("heads the card with how many checks pass and how many need you", () => {
    const { card } = renderCard();

    expect(card).toHaveTextContent("2 of 4 pass · 2 need you");
  });

  it("lists every check until an area is picked, the problems first and the worst of them first", () => {
    const { card } = renderCard();

    const rows = within(card).getAllByTestId("system-check");
    expect(rows).toHaveLength(4);
    expect(rows[0]).toHaveTextContent(RADARR);
    expect(rows[1]).toHaveTextContent("Movies needs a fix");
    expect(rows[1]).toHaveTextContent("Output folder missing · create it");
    expect(within(rows[1]).getByTitle(/does not exist/)).toHaveTextContent(
      "Output folder missing · create it",
    );
    expect(rows[1]).toHaveTextContent("checked 12s ago");
    expect(rows[2]).toHaveAttribute("data-status", "done");
  });

  it("does not say when a check was made if it has not been made yet", () => {
    checks.checks = [
      item("radarr", "connections", "broken", {
        title: RADARR,
        checkedAt: null,
      }),
    ];
    const { card } = renderCard();

    const [row] = within(card).getAllByTestId("system-check");
    expect(row).toHaveTextContent(RADARR);
    expect(row).not.toHaveTextContent(/checked|live/);
  });

  it("counts each area on the ribbon and lists all of an area's checks when it is picked", () => {
    const { card } = renderCard();
    const ribbon = within(card).getByRole("group", { name: "Health areas" });

    const workflows = within(ribbon).getByRole("button", { name: /Workflows/ });
    expect(workflows).toHaveTextContent("1/2");

    fireEvent.click(workflows);
    expect(workflows).toHaveAttribute("aria-pressed", "true");
    expect(within(card).getAllByTestId("system-check")).toHaveLength(2);
    expect(card).toHaveTextContent("TV");

    fireEvent.click(workflows);
    expect(within(card).getAllByTestId("system-check")).toHaveLength(4);
  });

  it("links 'Fix it' to the screen that sets the thing", () => {
    const { card } = renderCard();

    expect(
      within(card).getByRole("link", { name: "Fix it → Movies needs a fix" }),
    ).toHaveAttribute("href", "/setup/workflows?edit=2");
  });

  it("looks at one check again, and says so while it does", () => {
    const { card, rerender } = renderCard();

    fireEvent.click(
      within(card).getByRole("button", { name: `Check again: ${RADARR}` }),
    );
    expect(checks.again).toHaveBeenCalledWith(
      expect.objectContaining({ id: "radarr" }),
    );

    checks.busy = new Set(["radarr"]);
    rerender(<Card />);
    expect(
      within(card).getByRole("button", { name: `Check again: ${RADARR}` }),
    ).toBeDisabled();
  });

  it("opens a workflow's folder chain in a drawer from Details, and closes it again", () => {
    const { card } = renderCard();

    fireEvent.click(
      within(card).getByRole("button", { name: "Details: Movies needs a fix" }),
    );

    const drawer = screen.getByRole("dialog", { name: "Movies" });
    expect(drawer).toHaveTextContent("The output folder is missing.");
    expect(drawer).toHaveTextContent("Keeps 20.00 GB free where it writes.");
    expect(
      within(drawer).getByRole("link", { name: "Open this workflow" }),
    ).toHaveAttribute("href", "/setup/workflows?edit=2");

    fireEvent.click(within(drawer).getByRole("button", { name: "Close" }));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("offers Details only on a check about a workflow", () => {
    const { card } = renderCard();

    expect(
      within(card).queryByRole("button", { name: `Details: ${RADARR}` }),
    ).toBeNull();
  });

  it("says how many checks the card's height left out", () => {
    fits = 1;
    const { card } = renderCard();

    expect(within(card).getByTestId("system-more")).toHaveTextContent("3 more");
  });

  it("lists the passing checks, each saying when it was looked at, when nothing needs you", () => {
    checks.checks = [item("ffmpeg", "tools", "done", { title: "FFmpeg" })];
    const { card } = renderCard();

    expect(card).toHaveTextContent("1 of 1 pass · all good");
    expect(within(card).getByTestId("system-check")).toHaveTextContent(
      "checked 12s ago",
    );
  });

  it("says a drive is live instead of when it was looked at", () => {
    checks.checks = [
      item("drive", "storage", "done", {
        title: "D:",
        checkedAt: null,
        live: true,
      }),
    ];
    const { card } = renderCard();

    const row = within(card).getByTestId("system-check");
    expect(row).toHaveTextContent("Storage · live");
    expect(row).not.toHaveTextContent("checked");
  });

  it("says Weir is still looking while there is no check", () => {
    checks.checks = [];
    const { card } = renderCard();

    expect(card).toHaveTextContent("Weir is still looking.");
  });

  it("says nothing to show for an area that has no check", () => {
    const { card } = renderCard();

    fireEvent.click(within(card).getByRole("button", { name: /Storage/ }));

    expect(card).toHaveTextContent("Nothing to show for Storage.");
  });

  it("rings the card and goes back to every area when 'N need you' sends you to it", () => {
    const { card, rerender } = renderCard(0);
    fireEvent.click(within(card).getByRole("button", { name: /Tools/ }));
    expect(card).not.toHaveTextContent(RADARR);

    rerender(<Card jump={1} />);

    expect(card).toHaveClass("mm-sy-card--flash");
    expect(card).toHaveTextContent(RADARR);
  });

  describe.each([
    ["scrolls to the card smoothly", false, "smooth"],
    ["jumps to the card for a person who prefers less motion", true, "auto"],
  ])("when 'N need you' sends you to it, it %s", (_, reduced, behavior) => {
    it("scrolls the card into view", () => {
      const scrollIntoView = vi.fn();
      Element.prototype.scrollIntoView = scrollIntoView;
      vi.stubGlobal("matchMedia", () => ({ matches: reduced }));
      try {
        const { rerender } = renderCard(0);

        rerender(<Card jump={1} />);

        expect(scrollIntoView).toHaveBeenCalledWith({
          block: "nearest",
          behavior,
        });
      } finally {
        delete (Element.prototype as Partial<Element>).scrollIntoView;
        vi.unstubAllGlobals();
      }
    });
  });

  it("sweeps a sheen over the areas when the checks are read again, and not each time the server says it looked", () => {
    vi.useFakeTimers({ toFake: ["Date", "setTimeout", "clearTimeout"] });
    vi.setSystemTime(NOW);
    checks.readAt = NOW - 12_000;
    const { card, rerender } = renderCard();
    const ribbon = within(card).getByRole("group", { name: "Health areas" });
    const first = () => within(ribbon).getAllByRole("button")[0];
    expect(first()).not.toHaveClass("mm-sy-area--checking");

    checks.checks = checks.checks.map((entry) => ({
      ...entry,
      checkedAt: NOW,
    }));
    rerender(<Card />);
    expect(first()).not.toHaveClass("mm-sy-area--checking");

    checks.readAt = NOW;
    rerender(<Card />);
    expect(first()).toHaveClass("mm-sy-area--checking");

    act(() => {
      vi.advanceTimersByTime(SHEEN_MS);
    });
    expect(first()).not.toHaveClass("mm-sy-area--checking");
    checks.readAt = null;
  });
});
