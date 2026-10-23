import { act, fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, expect, it, vi } from "vitest";

import { fetchServerDirectories } from "../../lib/system/directory-browser-api";
import { ServerFolderPickerButton } from "./server-folder-picker-button";

vi.mock("../../lib/system/directory-browser-api", () => ({
  fetchServerDirectories: vi.fn(),
}));

const fetchDirectories = vi.mocked(fetchServerDirectories);

const DRIVES = {
  current_path: null,
  parent_path: null,
  entries: [{ name: "D:", path: "D:\\", kind: "root", description: null }],
};

beforeEach(() => {
  fetchDirectories.mockReset();
});

it("opens a dialog named by its title and hands back the chosen folder", async () => {
  fetchDirectories.mockResolvedValue(DRIVES);
  const onSelect = vi.fn();
  render(
    <ServerFolderPickerButton
      title="Choose the watched folder"
      value=""
      onSelect={onSelect}
    />,
  );

  fireEvent.click(screen.getByRole("button", { name: "Browse" }));
  expect(
    screen.getByRole("dialog", { name: "Choose the watched folder" }),
  ).toBeInTheDocument();
  fireEvent.click(await screen.findByRole("button", { name: "Select" }));

  expect(onSelect).toHaveBeenCalledWith("D:\\");
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
});

it("closes on Escape", async () => {
  fetchDirectories.mockResolvedValue(DRIVES);
  render(
    <ServerFolderPickerButton title="Choose" value="" onSelect={vi.fn()} />,
  );
  fireEvent.click(screen.getByRole("button", { name: "Browse" }));
  await screen.findByRole("button", { name: "Select" });

  fireEvent.keyDown(document, { key: "Escape" });

  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
});

it("falls back to the drive list when the saved folder cannot be opened", async () => {
  fetchDirectories
    .mockRejectedValueOnce(new Error("No such folder"))
    .mockResolvedValueOnce(DRIVES);
  render(
    <ServerFolderPickerButton
      title="Choose"
      value="/mnt/gone"
      onSelect={vi.fn()}
    />,
  );

  fireEvent.click(screen.getByRole("button", { name: "Browse" }));

  expect(
    await screen.findByText(
      '"/mnt/gone" could not be opened. Showing available drives instead.',
    ),
  ).toBeInTheDocument();
  expect(fetchDirectories).toHaveBeenLastCalledWith(null);
});

it("lets go of a folder list that arrives after the dialog has been closed", async () => {
  let arrive: (listing: typeof DRIVES) => void = () => undefined;
  fetchDirectories.mockReturnValue(
    new Promise((resolve) => {
      arrive = resolve;
    }),
  );
  render(
    <ServerFolderPickerButton title="Choose" value="" onSelect={vi.fn()} />,
  );
  fireEvent.click(screen.getByRole("button", { name: "Browse" }));
  fireEvent.keyDown(document, { key: "Escape" });

  await act(async () => arrive(DRIVES));

  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
});
