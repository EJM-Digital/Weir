import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { WithWorkflows } from "../../test/with-workflows";
import { FileName } from "./file-name";
import { Poster } from "./poster";
import { QuietFieldGroup } from "./quiet-section";
import { SettingsGroup } from "./settings-group";
import { SidePanel } from "./side-panel";

describe("a settings group", () => {
  it("shows its line on why it matters and its footer when it has them", () => {
    render(
      <SettingsGroup
        title="Backups"
        detail="Why this matters."
        footer={<button type="button">Save</button>}
      >
        <p>A setting</p>
      </SettingsGroup>,
    );

    expect(screen.getByText("Why this matters.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save" })).toBeInTheDocument();
  });

  it("shows neither when it has none", () => {
    const { container } = render(
      <SettingsGroup title="Backups">
        <p>A setting</p>
      </SettingsGroup>,
    );

    expect(container.querySelector(".mm-setgroup__detail")).toBeNull();
    expect(container.querySelector(".mm-quiet-actions")).toBeNull();
  });
});

describe("a field group", () => {
  it("takes the class it is given", () => {
    render(
      <QuietFieldGroup title="Folders" className="extra">
        <p>Fields</p>
      </QuietFieldGroup>,
    );

    expect(screen.getByRole("region", { name: "Folders" })).toHaveClass(
      "mm-quiet-group",
      "extra",
    );
  });
});

describe("a side panel", () => {
  it("shows an eyebrow and a subtitle when it has them, and neither when it has none", () => {
    const { rerender } = render(
      <SidePanel
        open
        title="Heat"
        eyebrow="Movies"
        subtitle="D:/films/Heat"
        onClose={() => undefined}
      >
        <p>Body</p>
      </SidePanel>,
    );
    expect(screen.getByText("Movies")).toBeInTheDocument();
    expect(screen.getByText("D:/films/Heat")).toBeInTheDocument();

    rerender(
      <SidePanel open title="Heat" onClose={() => undefined}>
        <p>Body</p>
      </SidePanel>,
    );
    expect(screen.queryByText("Movies")).toBeNull();
    expect(screen.queryByText("D:/films/Heat")).toBeNull();
  });
});

describe("a poster with nothing to take initials from", () => {
  it("falls back to the first letter of the title", () => {
    const { container } = render(
      <Poster url={null} title="The" workflow="Movies" />,
      { wrapper: WithWorkflows },
    );

    expect(container.querySelector("[aria-hidden]")).toHaveTextContent("T");
  });
});

describe("a file name without an extension", () => {
  it("is shown whole", () => {
    const { container } = render(<FileName path="D:/films/README" />);

    expect(container.querySelector("[data-file-name]")).toHaveTextContent(
      "README",
    );
  });
});
