import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { library } from "./library-test-fixtures";
import { OutputFolderCell } from "./library-paths";

describe("where a workflow writes its cleaned files", () => {
  it("shows the folder it writes to", () => {
    render(
      <OutputFolderCell
        library={library({ watched_folder: "/in", output_folder: "/out" })}
      />,
    );

    expect(screen.getByText("/out")).toBeInTheDocument();
  });

  it("says a workflow with no folders cleans files where they are", () => {
    render(
      <OutputFolderCell
        library={library({ watched_folder: "", output_folder: "" })}
      />,
    );

    expect(screen.getByText("Cleans in place")).toBeInTheDocument();
  });

  it("says a workflow that watches a folder but has nowhere to write needs an output folder", () => {
    render(
      <OutputFolderCell
        library={library({ watched_folder: "/in", output_folder: "" })}
      />,
    );

    expect(screen.getByText("Needs an output folder")).toHaveAttribute(
      "title",
      "Needs a folder to clean files into.",
    );
  });

  it("says too that the workflow is off for that reason when it is switched off", () => {
    render(
      <OutputFolderCell
        library={library({
          enabled: false,
          watched_folder: "/in",
          output_folder: "",
        })}
      />,
    );

    expect(screen.getByText("Needs an output folder")).toHaveAttribute(
      "title",
      "Needs a folder to clean files into, so it is off.",
    );
  });
});
