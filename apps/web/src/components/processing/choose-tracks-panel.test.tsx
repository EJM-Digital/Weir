import { fireEvent, render, screen } from "@testing-library/react";
import { expect, it, vi } from "vitest";

import { ChooseTracksPanel } from "./choose-tracks-panel";
import type { ProcessingFileTracks } from "../../lib/processing/files-api";

function tracks(
  over: Partial<ProcessingFileTracks> = {},
): ProcessingFileTracks {
  return {
    file_id: 1,
    relative_path: "Some Film/film.mkv",
    media_scope: "movie",
    source_fingerprint: {
      device: 0,
      inode: 0,
      size_bytes: 100,
      modified_time_ns: 1,
    },
    streams: [
      {
        index: 0,
        type: "video",
        codec: "h264",
        language: null,
        title: null,
        channels: null,
        default: true,
        forced: false,
        rule_would_keep: true,
        rule_reason: "Kept as the video track.",
      },
      {
        index: 1,
        type: "audio",
        codec: "aac",
        language: "eng",
        title: null,
        channels: 2,
        default: true,
        forced: false,
        rule_would_keep: true,
        rule_reason: "Kept: selected as the best audio track.",
      },
      {
        index: 2,
        type: "audio",
        codec: "aac",
        language: "jpn",
        title: null,
        channels: 2,
        default: false,
        forced: false,
        rule_would_keep: false,
        rule_reason: "Removed: not selected.",
      },
      {
        index: 3,
        type: "subtitle",
        codec: "subrip",
        language: "eng",
        title: null,
        channels: null,
        default: false,
        forced: false,
        rule_would_keep: false,
        rule_reason: "Removed: the saved rules remove every subtitle track.",
      },
    ],
    ...over,
  };
}

function baseProps() {
  return {
    open: true,
    fileName: "Some Film/film.mkv",
    tracks: tracks(),
    loading: false,
    loadError: null,
    submitting: false,
    submitError: null,
    onClose: vi.fn(),
    onSubmit: vi.fn(),
  };
}

it("shows no table of tracks before any have been read or failed to read", () => {
  render(<ChooseTracksPanel {...baseProps()} tracks={undefined} />);

  expect(screen.getByTestId("choose-tracks-panel")).toBeInTheDocument();
  expect(screen.queryByRole("table")).not.toBeInTheDocument();
  expect(screen.queryByTestId("choose-tracks-submit")).not.toBeInTheDocument();
});

it("renders nothing when closed", () => {
  const { container } = render(
    <ChooseTracksPanel {...baseProps()} open={false} />,
  );
  expect(container).toBeEmptyDOMElement();
});

it("seeds each row from what the saved rules would do", () => {
  render(<ChooseTracksPanel {...baseProps()} />);

  expect(screen.getByTestId("choose-tracks-keep-0")).toBeChecked();
  expect(screen.getByTestId("choose-tracks-keep-1")).toBeChecked();
  expect(screen.getByTestId("choose-tracks-keep-2")).not.toBeChecked();
  expect(screen.getByTestId("choose-tracks-keep-3")).not.toBeChecked();
  // The dropped section lists exactly what is not kept at the start, in words rather than raw codes.
  const dropped = screen.getByTestId("choose-tracks-dropped-list");
  expect(dropped).toHaveTextContent("Japanese");
  expect(dropped).toHaveTextContent("English");
});

it("refuses to submit with no video kept", () => {
  render(<ChooseTracksPanel {...baseProps()} />);

  fireEvent.click(screen.getByTestId("choose-tracks-keep-0"));

  expect(
    screen.getByText("Keep at least one video track."),
  ).toBeInTheDocument();
  expect(screen.getByTestId("choose-tracks-submit")).toBeDisabled();
});

it("refuses to submit with no audio kept", () => {
  render(<ChooseTracksPanel {...baseProps()} />);

  fireEvent.click(screen.getByTestId("choose-tracks-keep-1"));

  expect(
    screen.getByText("Keep at least one audio track."),
  ).toBeInTheDocument();
  expect(screen.getByTestId("choose-tracks-submit")).toBeDisabled();
});

it("moves a kept track earlier and later with the order buttons", () => {
  render(<ChooseTracksPanel {...baseProps()} />);

  // Keep the Japanese track too, so there is something to reorder against the English one.
  fireEvent.click(screen.getByTestId("choose-tracks-keep-2"));
  const order = screen.getAllByTestId(/^choose-tracks-order-/);
  expect(order.map((row) => row.dataset.testid)).toEqual([
    "choose-tracks-order-0",
    "choose-tracks-order-1",
    "choose-tracks-order-2",
  ]);

  fireEvent.click(screen.getByTestId("choose-tracks-move-up-2"));

  const reordered = screen.getAllByTestId(/^choose-tracks-order-/);
  expect(reordered.map((row) => row.dataset.testid)).toEqual([
    "choose-tracks-order-0",
    "choose-tracks-order-2",
    "choose-tracks-order-1",
  ]);
});

it("submits exactly the kept indices, their flags and the chosen order", () => {
  const onSubmit = vi.fn();
  render(<ChooseTracksPanel {...baseProps()} onSubmit={onSubmit} />);

  fireEvent.click(screen.getByTestId("choose-tracks-submit"));

  expect(onSubmit).toHaveBeenCalledWith({
    keep: [
      { index: 0, default: false, forced: false },
      { index: 1, default: true, forced: false },
    ],
    order: [0, 1],
  });
});

it("marking a different audio track default clears the previous one", () => {
  const onSubmit = vi.fn();
  render(<ChooseTracksPanel {...baseProps()} onSubmit={onSubmit} />);
  fireEvent.click(screen.getByTestId("choose-tracks-keep-2"));

  fireEvent.click(screen.getByTestId("choose-tracks-default-2"));
  fireEvent.click(screen.getByTestId("choose-tracks-submit"));

  const call = onSubmit.mock.calls[0][0] as {
    keep: { index: number; default: boolean }[];
  };
  const byIndex = new Map(call.keep.map((k) => [k.index, k.default]));
  expect(byIndex.get(1)).toBe(false);
  expect(byIndex.get(2)).toBe(true);
});

it("shows the load error instead of a table when the probe failed", () => {
  render(
    <ChooseTracksPanel
      {...baseProps()}
      tracks={undefined}
      loadError="Weir could not read this file's tracks."
    />,
  );

  expect(
    screen.getByText("Weir could not read this file's tracks."),
  ).toBeInTheDocument();
  expect(screen.queryByRole("table")).not.toBeInTheDocument();
});

function columnNames() {
  return screen
    .getAllByRole("columnheader")
    .map((heading) => heading.textContent);
}

it("keeps the tracks in the order the file holds them, with no heading that sorts", () => {
  localStorage.clear();
  render(<ChooseTracksPanel {...baseProps()} />);

  expect(
    screen.queryByRole("button", { name: "Track" }),
  ).not.toBeInTheDocument();
  expect(
    screen
      .getAllByTestId(/^choose-tracks-row-/)
      .map((row) => row.dataset.testid),
  ).toEqual([
    "choose-tracks-row-0",
    "choose-tracks-row-1",
    "choose-tracks-row-2",
    "choose-tracks-row-3",
  ]);
});

it("moves a column from the keyboard and puts the columns back with Reset columns", () => {
  localStorage.clear();
  render(<ChooseTracksPanel {...baseProps()} />);
  const track = screen.getByRole("columnheader", { name: "Track" });

  fireEvent.keyDown(track, { key: "ArrowLeft", altKey: true });
  expect(columnNames().slice(0, 2)).toEqual(["Track", "Keep"]);

  fireEvent.click(screen.getByRole("button", { name: "Columns" }));
  fireEvent.click(screen.getByRole("menuitem", { name: "Reset columns" }));
  expect(columnNames().slice(0, 2)).toEqual(["Keep", "Track"]);
});

it("closes from Cancel, and says nothing about queueing", () => {
  const props = baseProps();
  render(<ChooseTracksPanel {...props} />);

  fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

  expect(props.onClose).toHaveBeenCalledTimes(1);
  expect(props.onSubmit).not.toHaveBeenCalled();
});

it("says Queueing… and holds both buttons while the choice is being queued", () => {
  const props = baseProps();
  render(<ChooseTracksPanel {...props} submitting />);

  const queueing = screen.getByTestId("choose-tracks-submit");
  expect(queueing).toHaveTextContent("Queueing…");
  expect(queueing).toBeDisabled();
  expect(screen.getByRole("button", { name: "Cancel" })).toBeDisabled();
  fireEvent.click(queueing);
  expect(props.onSubmit).not.toHaveBeenCalled();
});

it("shows why the choice could not be queued, and leaves the buttons ready to try again", () => {
  const props = baseProps();
  render(
    <ChooseTracksPanel
      {...props}
      submitError="Weir could not queue that choice. The file changed."
    />,
  );

  expect(screen.getByTestId("choose-tracks-submit-error")).toHaveTextContent(
    "Weir could not queue that choice. The file changed.",
  );
  expect(screen.getByTestId("choose-tracks-submit-error")).toHaveAttribute(
    "role",
    "alert",
  );
  expect(screen.getByTestId("choose-tracks-submit")).toBeEnabled();
  expect(screen.getByRole("button", { name: "Cancel" })).toBeEnabled();
});

it("shows no queueing error when there is none", () => {
  render(<ChooseTracksPanel {...baseProps()} />);

  expect(
    screen.queryByTestId("choose-tracks-submit-error"),
  ).not.toBeInTheDocument();
});

it("lists an embedded image or attachment apart from the table, with what the saved rules do with it, and does not offer it", () => {
  const withImages = tracks();
  withImages.streams.push(
    {
      index: 4,
      type: "image",
      codec: "mjpeg",
      language: null,
      title: "Poster",
      channels: null,
      default: false,
      forced: false,
      rule_would_keep: false,
      rule_reason: "This pass does not act on this stream type.",
    },
    {
      index: 5,
      type: "attachment",
      codec: "ttf",
      language: null,
      title: null,
      channels: null,
      default: false,
      forced: false,
      rule_would_keep: true,
      rule_reason: "Fonts stay so styled subtitles still display.",
    },
  );
  render(<ChooseTracksPanel {...baseProps()} tracks={withImages} />);

  const disclosure = screen.getByText(
    /2 embedded images or attachments not shown above/,
  );
  const items = [...disclosure.closest("details")!.querySelectorAll("li")].map(
    (item) => item.textContent,
  );
  expect(items).toEqual([
    "Cover image “Poster” — removed: This pass does not act on this stream type.",
    "Attachment — kept: Fonts stay so styled subtitles still display.",
  ]);
  expect(screen.getAllByRole("row")).toHaveLength(1 + 4);
});

it("calls a stream of the kind other an other stream", () => {
  const withData = tracks();
  withData.streams.push({
    index: 4,
    type: "other",
    codec: "bin_data",
    language: null,
    title: null,
    channels: null,
    default: false,
    forced: false,
    rule_would_keep: false,
    rule_reason: "This pass does not act on this stream type.",
  });
  render(<ChooseTracksPanel {...baseProps()} tracks={withData} />);

  expect(
    screen.getByText(
      "Other stream — removed: This pass does not act on this stream type.",
    ),
  ).toBeInTheDocument();
});

it("has no list of images or attachments when the file has none", () => {
  render(<ChooseTracksPanel {...baseProps()} />);

  expect(screen.queryByText(/embedded image/)).not.toBeInTheDocument();
});
