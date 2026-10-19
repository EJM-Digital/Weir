import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { LibraryFolderChain } from "../../lib/processing/library-folder-chain-api";
import { EmptyState } from "./empty-state";
import { FactTable } from "./fact-table";
import { FolderChainSections } from "./folder-chain-sections";
import {
  QuietDisclosure,
  QuietFieldGroup,
  QuietSection,
  quietActionRowClass,
} from "./quiet-section";
import { SettingRow, SettingsGroup, NumberWithUnit } from "./settings-group";
import { SetupCheckLines } from "./setup-check-lines";
import { WorkflowKindBadge, WorkflowKindSummary } from "./workflow-kind";

describe("EmptyState", () => {
  it("says what the list is for under its title and offers the one action, where the list will be", () => {
    render(
      <EmptyState
        title="No alerts yet"
        action={<button type="button">Add an alert</button>}
        testId="empty-alerts"
      >
        Alerts tell you when something needs you.
      </EmptyState>,
    );

    const section = screen.getByTestId("empty-alerts");
    expect(
      within(section).getByRole("heading", { level: 2, name: "No alerts yet" }),
    ).toBeVisible();
    expect(section).toHaveTextContent(
      "Alerts tell you when something needs you.",
    );
    expect(
      within(section).getByRole("button", { name: "Add an alert" }),
    ).toBeVisible();
  });
});

describe("SettingsGroup and its rows", () => {
  function Group({ onChange }: { onChange: (next: string) => void }) {
    return (
      <SettingsGroup
        title="Speed"
        detail="How much Weir does at once."
        aside={<span>aside</span>}
        footer={<button type="button">Save</button>}
        testId="group"
      >
        <SettingRow
          label="Files at once"
          hint="Two is a good start."
          htmlFor="n"
        >
          <NumberWithUnit
            id="n"
            value="2"
            unit="files"
            min={1}
            max={8}
            step={1}
            onChange={onChange}
          />
        </SettingRow>
        <SettingRow label="Pause between" stacked>
          <button type="button">Wide control</button>
        </SettingRow>
      </SettingsGroup>
    );
  }

  it("titles the group, says why it matters, and puts its own actions under the rows", () => {
    render(<Group onChange={vi.fn()} />);

    const group = screen.getByTestId("group");
    expect(within(group).getByRole("heading", { name: "Speed" })).toBeVisible();
    expect(group).toHaveTextContent("How much Weir does at once.");
    const save = within(group).getByRole("button", { name: "Save" });
    expect(save.parentElement).toHaveClass(quietActionRowClass);
  });

  it("names a row's number by its label, shows its unit after it, and hands back what is typed as text", () => {
    const onChange = vi.fn();
    render(<Group onChange={onChange} />);

    const input = screen.getByLabelText("Files at once");
    expect(input).toHaveValue(2);
    expect(input).toHaveAttribute("min", "1");
    expect(input).toHaveAttribute("max", "8");
    expect(input).toHaveAttribute("step", "1");
    expect(input.parentElement).toHaveTextContent("files");
    expect(screen.getByText("Two is a good start.")).toBeVisible();
    fireEvent.change(input, { target: { value: "5" } });
    expect(onChange).toHaveBeenCalledWith("5");
  });

  it("puts a control that labels itself under its name when the row is stacked", () => {
    render(<Group onChange={vi.fn()} />);

    const row = screen.getByText("Pause between").closest(".mm-setrow");
    expect(row).toHaveClass("mm-setrow--stacked");
    expect(
      screen.getByText("Files at once").closest(".mm-setrow"),
    ).not.toHaveClass("mm-setrow--stacked");
  });

  it("leaves a number alone when it is disabled", () => {
    render(
      <NumberWithUnit
        id="n"
        value="2"
        unit="files"
        disabled
        onChange={vi.fn()}
      />,
    );

    expect(screen.getByRole("spinbutton")).toBeDisabled();
  });
});

describe("the quiet page parts", () => {
  it("heads a section at the level it sits at, with a count after the heading", () => {
    render(
      <QuietSection
        headingId="s"
        heading="Workflows"
        level={3}
        count="3 in all"
      >
        <p>Body</p>
      </QuietSection>,
    );

    expect(
      screen.getByRole("heading", { level: 3, name: "Workflows" }),
    ).toHaveAttribute("id", "s");
    expect(screen.getByText("3 in all")).toBeVisible();
    expect(screen.getByText("Body")).toBeVisible();
  });

  it("names a group of fields by its title alone, and keeps the step number out of that name", () => {
    render(
      <QuietFieldGroup step={2} title="Folders" detail="Where files live.">
        <p>Fields</p>
      </QuietFieldGroup>,
    );

    const group = screen.getByRole("region", { name: "Folders" });
    expect(group).toHaveTextContent("Where files live.");
    expect(screen.getByText("2")).toHaveAttribute("aria-hidden", "true");
    expect(screen.getByRole("heading", { name: "Folders" })).toBeVisible();
  });

  it("starts a disclosure closed, with its state in the heading line, and its detail inside once opened", () => {
    const { container } = render(
      <QuietDisclosure
        title="More choices"
        summaryWhenClosed="3 of 9 on"
        detail="Few people change these."
      >
        <p>Inside</p>
      </QuietDisclosure>,
    );

    const details = container.querySelector("details")!;
    expect(details.open).toBe(false);
    expect(
      screen.getByRole("heading", { name: "More choices" }),
    ).toBeInTheDocument();
    expect(screen.getByText("3 of 9 on")).toBeInTheDocument();
    details.open = true;
    expect(
      screen.getByRole("region", { name: "More choices" }),
    ).toHaveTextContent("Few people change these.");
  });

  it("starts a disclosure open when asked to", () => {
    const { container } = render(
      <QuietDisclosure title="More choices" defaultOpen>
        <p>Inside</p>
      </QuietDisclosure>,
    );

    expect(container.querySelector("details")!.open).toBe(true);
  });
});

describe("FactTable", () => {
  it("lays facts out as one row under their names, with a note under a value that has one", () => {
    render(
      <FactTable
        caption="About this Weir"
        data-testid="facts"
        facts={[
          { label: "Version", value: "3.2.16" },
          { label: "Database", value: "Ready", detail: "Migrated today" },
        ]}
      />,
    );

    const table = screen.getByRole("table", { name: "About this Weir" });
    expect(table).toHaveAttribute("data-testid", "facts");
    expect(
      within(table)
        .getAllByRole("columnheader")
        .map((cell) => cell.textContent),
    ).toEqual(["Version", "Database"]);
    const cells = within(table).getAllByRole("cell");
    expect(cells[0]).toHaveTextContent("3.2.16");
    expect(cells[0]).toHaveAttribute("data-label", "Version");
    expect(cells[1]).toHaveTextContent("ReadyMigrated today");
  });
});

describe("the workflow kind", () => {
  it("badges a workflow no media manager feeds as Weir only", () => {
    render(<WorkflowKindBadge kind={{ kind: "weir_only" }} />);

    expect(screen.getByTestId("workflow-kind-badge")).toHaveTextContent(
      "Weir only",
    );
  });

  it("badges a linked workflow with the managers it is linked to, and explains what each does", () => {
    render(
      <WorkflowKindSummary
        kind={{
          kind: "linked",
          managers: [{ id: 5, name: "Deluno on rig", kind: "deluno" }],
        }}
      />,
    );

    expect(screen.getByTestId("workflow-kind-badge")).toHaveTextContent(
      "Linked to Deluno on rig",
    );
    expect(
      screen.getByText(/Deluno on rig hands each finished download to Weir/),
    ).toBeVisible();
  });

  it("explains a Weir only workflow in a line", () => {
    render(<WorkflowKindSummary kind={{ kind: "weir_only" }} />);

    expect(
      screen.getByText(/Files that appear in the watched folder are cleaned/),
    ).toBeVisible();
  });
});

describe("SetupCheckLines", () => {
  it("marks each finding by what it means, and says so in words to a screen reader", () => {
    render(
      <SetupCheckLines
        label="Deluno"
        lines={[
          { state: "ok", text: "Reachable." },
          { state: "problem", text: "Folder missing." },
          { state: "note", text: "Set in Deluno." },
          { state: "unverified", text: "Could not look." },
        ]}
      />,
    );

    const list = screen.getByRole("list", { name: "Deluno check" });
    const items = within(list).getAllByRole("listitem");
    expect(items.map((item) => item.textContent)).toEqual([
      "✓Fine: Reachable.",
      "✗Needs a fix: Folder missing.",
      "·Note: Set in Deluno.",
      "?Not verified: Could not look.",
    ]);
    expect(items.map((item) => item.getAttribute("data-status"))).toEqual([
      "done",
      "attention",
      "idle",
      "attention",
    ]);
  });
});

describe("FolderChainSections", () => {
  const chain: LibraryFolderChain = {
    library_id: 1,
    ready: false,
    local: {
      ready: true,
      lines: [{ state: "ok", text: "Weir can read the folder." }],
    },
    managers: [
      {
        connection_id: 5,
        label: "Deluno on rig",
        ready: true,
        lines: [{ state: "unverified", text: "Could not look." }],
      },
    ],
    download_clients: [
      {
        connection_id: 8,
        label: "SABnzbd",
        ready: false,
        lines: [{ state: "problem", text: "Not saving here." }],
      },
    ],
  } as LibraryFolderChain;

  it("groups the lines: Weir's own folders, then each media manager, then each download client, each with how ready it is", () => {
    render(<FolderChainSections chain={chain} />);

    const sections = screen.getAllByRole("region");
    expect(sections.map((s) => s.getAttribute("aria-label"))).toEqual([
      "Weir's own folders",
      "Deluno on rig",
      "SABnzbd",
    ]);
    expect(sections[0]).toHaveTextContent("Ready");
    expect(sections[1]).toHaveTextContent("Not verified");
    expect(sections[2]).toHaveTextContent("Needs attention");
    expect(
      within(sections[2]!).getByRole("list", { name: "SABnzbd check" }),
    ).toHaveTextContent("Not saving here.");
  });
});
