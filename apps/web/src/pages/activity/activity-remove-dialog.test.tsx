import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { ProcessingFileRemoveOptions } from "../../lib/processing/files-api";
import { ActivityRemoveDialog } from "./activity-remove-dialog";

vi.mock("../../lib/settings/queries", () => ({
  useAppSettingsQuery: () => ({ data: undefined }),
}));

function options(
  partial: Partial<ProcessingFileRemoveOptions> = {},
): ProcessingFileRemoveOptions {
  return {
    requires_choice: true,
    manager_label: null,
    delete_handled_by_manager: false,
    keep_notifies_manager: false,
    fingerprint_recorded: true,
    unconfirmed_size_bytes: null,
    unconfirmed_modified_at: null,
    delete_refused_reason: null,
    ...partial,
  };
}

function renderDialog(
  partial: Partial<ProcessingFileRemoveOptions> = {},
  props: { busy?: boolean; error?: string | null } = {},
) {
  const onCancel = vi.fn();
  const onConfirm = vi.fn();
  render(
    <ActivityRemoveDialog
      fileName="Film (2024).mkv"
      options={options(partial)}
      busy={props.busy ?? false}
      error={props.error ?? null}
      onCancel={onCancel}
      onConfirm={onConfirm}
    />,
  );
  return { onCancel, onConfirm };
}

const choice = (name: string) =>
  screen.getByTestId(`activity-remove-dialog-choice-${name}`);

describe("ActivityRemoveDialog", () => {
  it("names the file and offers the four choices with nothing chosen", () => {
    renderDialog();

    expect(
      screen.getByRole("dialog", {
        name: 'Remove "Film (2024).mkv" from Activity?',
      }),
    ).toBeInTheDocument();
    expect(
      within(choice("delete")).getByText("Delete the file"),
    ).toBeInTheDocument();
    expect(
      within(choice("keep")).getByText(
        "Keep the file, but don't process it again",
      ),
    ).toBeInTheDocument();
    expect(
      within(choice("retry")).getByText("Try it again"),
    ).toBeInTheDocument();
    expect(
      within(choice("remove")).getByText("Just remove it from the list"),
    ).toBeInTheDocument();
    for (const radio of screen.getAllByRole("radio")) {
      expect(radio).not.toBeChecked();
    }
    expect(screen.getByRole("button", { name: "Remove" })).toBeDisabled();
  });

  it("says what each choice does to the file", () => {
    renderDialog();

    expect(
      within(choice("delete")).getByText("Weir alone: the file is deleted."),
    ).toBeInTheDocument();
    expect(
      within(choice("keep")).getByText("Weir leaves it until it changes."),
    ).toBeInTheDocument();
    expect(
      within(choice("retry")).getByText("Weir processes it on the next pass."),
    ).toBeInTheDocument();
    expect(
      within(choice("remove")).getByText(
        "Leaves the file exactly as it is. Weir stops watching it, so a later scan could pick it up again.",
      ),
    ).toBeInTheDocument();
  });

  it("names the manager that will delete the download and search again, when one will", () => {
    renderDialog({ manager_label: "Radarr", delete_handled_by_manager: true });

    expect(
      within(choice("delete")).getByText(
        "Delete the download and ask Radarr for another copy",
      ),
    ).toBeInTheDocument();
    expect(
      within(choice("delete")).getByText(
        "Radarr removes it, blocks this release and searches again.",
      ),
    ).toBeInTheDocument();
  });

  it("deletes the file itself when a manager is linked but does not handle the delete, or none is named", () => {
    renderDialog({ manager_label: "Radarr", delete_handled_by_manager: false });
    expect(
      within(choice("delete")).getByText("Delete the file"),
    ).toBeInTheDocument();
  });

  it("deletes the file itself when the manager handles deletes but is not named", () => {
    renderDialog({ manager_label: null, delete_handled_by_manager: true });
    expect(
      within(choice("delete")).getByText("Delete the file"),
    ).toBeInTheDocument();
  });

  it("tells the manager it will not be imported only when the setting says so and the manager is named", () => {
    renderDialog({ manager_label: "Radarr", keep_notifies_manager: true });
    expect(
      within(choice("keep")).getByText(
        "Weir leaves it until it changes. Radarr is told it won't be imported.",
      ),
    ).toBeInTheDocument();
  });

  it("does not mention the manager on keep when it is not told", () => {
    renderDialog({ manager_label: "Radarr", keep_notifies_manager: false });
    expect(
      within(choice("keep")).getByText("Weir leaves it until it changes."),
    ).toBeInTheDocument();
  });

  it("does not offer a delete the server refused, and says why", () => {
    renderDialog({ delete_refused_reason: "Sonarr is still downloading it." });

    expect(
      within(choice("delete")).getByText("Sonarr is still downloading it."),
    ).toBeInTheDocument();
    expect(within(choice("delete")).getByRole("radio")).toBeDisabled();
    expect(within(choice("keep")).getByRole("radio")).toBeEnabled();
  });

  it("puts focus on the first choice that can be picked", () => {
    renderDialog({ delete_refused_reason: "Sonarr is still downloading it." });

    expect(within(choice("keep")).getByRole("radio")).toHaveFocus();
  });

  it("puts focus on delete when it can be picked", () => {
    renderDialog();

    expect(within(choice("delete")).getByRole("radio")).toHaveFocus();
  });

  it("marks only the chosen option and passes it on when confirmed", () => {
    const { onConfirm } = renderDialog();

    fireEvent.click(within(choice("retry")).getByRole("radio"));

    expect(choice("retry")).toHaveClass("mm-dialog-choice--chosen");
    expect(choice("delete")).not.toHaveClass("mm-dialog-choice--chosen");
    expect(choice("keep")).not.toHaveClass("mm-dialog-choice--chosen");
    expect(choice("remove")).not.toHaveClass("mm-dialog-choice--chosen");
    expect(choice("retry").className).toBe(
      "mm-dialog-choice mm-dialog-choice--chosen",
    );
    expect(choice("delete").className).toBe("mm-dialog-choice");
    expect(onConfirm).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Remove" }));
    expect(onConfirm).toHaveBeenCalledExactlyOnceWith("retry");
  });

  it("cancels without choosing anything", () => {
    const { onCancel, onConfirm } = renderDialog();

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    expect(onCancel).toHaveBeenCalledTimes(1);
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it("shows the file's details for a title that had none recorded, so it can be checked before deleting", () => {
    renderDialog({
      fingerprint_recorded: false,
      unconfirmed_size_bytes: 2 * 1024 * 1024,
      unconfirmed_modified_at: "2026-03-04T05:06:07Z",
    });

    const details = screen.getByTestId("activity-remove-dialog-unconfirmed");
    expect(details).toHaveTextContent(/2(\.0)? MB/);
    expect(details).toHaveTextContent("· modified");
    expect(details).toHaveTextContent("2026");
    expect(details).toHaveTextContent(
      "Weir didn't note this file's details when it failed, so check it's the one you mean.",
    );
  });

  it("shows the size alone when the modified time is not known, and shows nothing when the details were recorded", () => {
    renderDialog({
      fingerprint_recorded: false,
      unconfirmed_size_bytes: 1024,
      unconfirmed_modified_at: null,
    });

    const details = screen.getByTestId("activity-remove-dialog-unconfirmed");
    expect(details).not.toHaveTextContent("modified");
    expect(details.textContent).not.toContain("Stryker");
  });

  it("shows no details block when the file's details were recorded", () => {
    renderDialog({ fingerprint_recorded: true });

    expect(
      screen.queryByTestId("activity-remove-dialog-unconfirmed"),
    ).not.toBeInTheDocument();
  });

  it("shows why the removal failed and that it is under way", () => {
    renderDialog({}, { busy: true, error: "The server did not answer." });

    expect(screen.getByRole("alert")).toHaveTextContent(
      "The server did not answer.",
    );
    expect(screen.getByRole("button", { name: "Removing…" })).toBeDisabled();
  });
});
