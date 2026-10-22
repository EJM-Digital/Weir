import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import {
  fetchServerDirectories,
  type DirectoryBrowseResponse,
} from "../../lib/system/directory-browser-api";
import { ServerFolderPickerButton } from "./server-folder-picker-button";

vi.mock("../../lib/system/directory-browser-api", () => ({
  fetchServerDirectories: vi.fn(),
}));

const fetchDirectories = vi.mocked(fetchServerDirectories);

const MEDIA: DirectoryBrowseResponse = {
  current_path: "D:\\Media",
  parent_path: "D:\\",
  entries: [
    {
      name: "Films",
      path: "D:\\Media\\Films",
      kind: "directory",
      description: null,
    },
    {
      name: "Share",
      path: "\\\\nas\\share",
      kind: "network",
      description: "Network share",
    },
  ],
};

const DRIVES: DirectoryBrowseResponse = {
  current_path: null,
  parent_path: null,
  entries: [{ name: "D:", path: "D:\\", kind: "root", description: null }],
};

function openPicker(value = "") {
  const onSelect = vi.fn();
  render(
    <ServerFolderPickerButton
      title="Choose"
      value={value}
      onSelect={onSelect}
    />,
  );
  fireEvent.click(screen.getByRole("button", { name: "Browse" }));
  return onSelect;
}

beforeEach(() => {
  fetchDirectories.mockReset();
});

describe("when the folders cannot be listed", () => {
  it("says so when even the drive list fails", async () => {
    fetchDirectories.mockRejectedValue(new Error("Access denied"));
    openPicker();

    expect(await screen.findByRole("alert")).toHaveTextContent("Access denied");
  });

  it("shows the first failure when the saved folder and the drive list both fail", async () => {
    fetchDirectories
      .mockRejectedValueOnce(new Error("No such folder"))
      .mockRejectedValueOnce(new Error("Drives unavailable"));
    openPicker("/mnt/gone");

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "No such folder",
    );
  });

  it("says a folder with no folders in it has none", async () => {
    fetchDirectories.mockResolvedValue({ ...MEDIA, entries: [] });
    openPicker("D:\\Media");

    expect(
      await screen.findByText("No folders found here."),
    ).toBeInTheDocument();
  });
});

describe("moving around the folders", () => {
  it("opens a folder from its name or its Open button, and shows a share by what it is", async () => {
    fetchDirectories.mockResolvedValue(MEDIA);
    openPicker("D:\\Media");
    expect(await screen.findByText("Network share")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /Films/ }));
    expect(fetchDirectories).toHaveBeenLastCalledWith("D:\\Media\\Films");

    const [, openShare] = await screen.findAllByRole("button", {
      name: "Open",
    });
    fireEvent.click(openShare);
    expect(fetchDirectories).toHaveBeenLastCalledWith("\\\\nas\\share");
  });

  it("goes up to the parent folder, and to the drives", async () => {
    fetchDirectories.mockResolvedValue(MEDIA);
    openPicker("D:\\Media");
    const up = await screen.findByRole("button", { name: "Up" });

    fireEvent.click(up);
    expect(fetchDirectories).toHaveBeenLastCalledWith("D:\\");

    const drives = screen.getByRole("button", { name: "Drives" });
    await waitFor(() => expect(drives).toBeEnabled());
    fireEvent.click(drives);
    expect(fetchDirectories).toHaveBeenLastCalledWith(null);
  });

  it("offers no way up from the drive list", async () => {
    fetchDirectories.mockResolvedValue(DRIVES);
    openPicker();
    await screen.findByText("D:");

    expect(screen.getByRole("button", { name: "Up" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Drives" })).toBeDisabled();
    expect(
      screen.queryByRole("button", { name: "Use this folder" }),
    ).not.toBeInTheDocument();
  });

  it("goes to a path that is typed, trimmed, and ignores a blank one", async () => {
    fetchDirectories.mockResolvedValue(MEDIA);
    openPicker("D:\\Media");
    await screen.findByText("Films");
    const callsBefore = fetchDirectories.mock.calls.length;

    const box = screen.getByRole("textbox", { name: "Folder path" });
    fireEvent.change(box, { target: { value: "   " } });
    fireEvent.click(screen.getByRole("button", { name: "Go to path" }));
    expect(fetchDirectories).toHaveBeenCalledTimes(callsBefore);

    fireEvent.change(box, { target: { value: "  /media/tv  " } });
    fireEvent.click(screen.getByRole("button", { name: "Go to path" }));
    expect(fetchDirectories).toHaveBeenLastCalledWith("/media/tv");
  });

  it("hands back the folder being looked at", async () => {
    fetchDirectories.mockResolvedValue(MEDIA);
    const onSelect = openPicker("D:\\Media");

    fireEvent.click(
      await screen.findByRole("button", { name: "Use this folder" }),
    );

    expect(onSelect).toHaveBeenCalledWith("D:\\Media");
  });
});
