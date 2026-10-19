import {
  cleanup,
  fireEvent,
  render,
  screen,
  within,
} from "@testing-library/react";
import { expect, it, vi } from "vitest";

import type { ProcessingFileLog } from "../../lib/processing/files-api";
import { WithWorkflows } from "../../test/with-workflows";
import { FileStoryPanel } from "./file-story-panel";

vi.mock("../../lib/settings/queries", () => ({
  useAppSettingsQuery: () => ({ data: undefined }),
}));

function log(over: Partial<ProcessingFileLog> = {}): ProcessingFileLog {
  return {
    file_id: 1,
    relative_path: "movies/Arrival.mkv",
    retention_days: 90,
    entries: [
      {
        id: 1,
        recorded_at: "2026-08-16T14:02:00Z",
        outcome: "live_output_written",
        title: "Remuxed Arrival",
        library_name: "Films 4K",
        detail: { ffmpeg_argv: ["ffmpeg", "-c", "copy"] },
        story: [
          {
            heading: "Picked up",
            sentence: "Weir took this file as a film in the Films 4K workflow.",
            tone: "neutral",
          },
          {
            heading: "Planned",
            sentence:
              "Weir planned to remove 2 audio tracks. The video is copied, not re-encoded, so picture quality is unchanged.",
            tone: "neutral",
          },
          { heading: "Checked", sentence: "It was complete.", tone: "good" },
        ],
      },
    ],
    ...over,
  };
}

function mount(
  props: Partial<React.ComponentProps<typeof FileStoryPanel>> = {},
) {
  const onClose = vi.fn();
  render(
    <WithWorkflows>
      <FileStoryPanel
        open
        fileName="Arrival.mkv"
        log={log()}
        loading={false}
        error={null}
        onClose={onClose}
        {...props}
      />
    </WithWorkflows>,
  );
  return { onClose };
}

it("tells the story in plain steps", () => {
  mount();
  expect(
    screen.getByRole("dialog", { name: "Arrival.mkv" }),
  ).toBeInTheDocument();
  expect(screen.getByText("Picked up")).toBeInTheDocument();
  expect(screen.getByText(/copied, not re-encoded/)).toBeInTheDocument();
});

it("shows a rejection as a Rejected step with its reason, not as Could not finish", () => {
  mount({
    log: log({
      entries: [
        {
          id: 2,
          recorded_at: "2026-08-16T14:02:00Z",
          outcome: "failed_before_execution",
          title: "Rejected Arrival.mkv",
          library_name: "Films 4K",
          detail: {},
          story: [
            {
              heading: "Rejected",
              sentence:
                "This file has no audio tracks, so there would be nothing to keep. The file was left where it is.",
              tone: "warn",
            },
          ],
        },
      ],
    }),
  });

  expect(screen.getByText("Rejected")).toBeInTheDocument();
  expect(
    screen.getByText(/The file was left where it is\./),
  ).toBeInTheDocument();
  expect(screen.queryByText("Could not finish")).not.toBeInTheDocument();
});

it("keeps the technical detail behind a disclosure, never leading", () => {
  mount();
  const summary = screen.getByText("Technical detail");
  expect(summary.closest("details")).not.toHaveAttribute("open");
});

it("says how long a file's activity is kept once the file is gone", () => {
  mount();
  expect(
    screen.getByText("Kept while the file exists, then 90 days."),
  ).toBeInTheDocument();
});

it("closes on Escape", () => {
  const { onClose } = mount();
  fireEvent.keyDown(document, { key: "Escape" });
  expect(onClose).toHaveBeenCalledTimes(1);
});

it("closes from the backdrop and the close button", () => {
  const { onClose } = mount();
  fireEvent.click(screen.getByRole("button", { name: "Close file activity" }));
  fireEvent.click(screen.getByRole("button", { name: "Close" }));
  expect(onClose).toHaveBeenCalledTimes(2);
});

it("takes focus when it opens", () => {
  mount();
  expect(screen.getByRole("dialog")).toHaveFocus();
});

it("explains an empty record rather than showing nothing", () => {
  mount({ log: log({ entries: [] }) });
  expect(screen.getByText(/hasn.t worked on this file/)).toBeInTheDocument();
});

it("says it is loading while the record is read, and claims nothing about the file yet", () => {
  mount({ log: undefined, loading: true });

  expect(screen.getByText("Loading…")).toBeInTheDocument();
  expect(screen.queryByText(/hasn.t worked on this file/)).toBeNull();
  expect(screen.queryByRole("alert")).toBeNull();
  expect(screen.queryByText("Technical detail")).toBeNull();
});

it("shows a failure to load", () => {
  mount({
    log: undefined,
    error: "Could not read that file's processing record",
  });
  expect(screen.getByRole("alert")).toHaveTextContent("Could not read");
});

it("renders nothing while closed", () => {
  mount({ open: false });
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
});

it("shows the title's poster beside the name, and the initials when it has none", () => {
  mount({
    poster: {
      url: "/api/v1/artwork/posters/movie-arrival-2016",
      workflow: "Films",
    },
  });

  expect(screen.getByRole("img", { name: "Arrival.mkv" })).toHaveAttribute(
    "src",
    "/api/v1/artwork/posters/movie-arrival-2016",
  );
});

it("shows no poster for a file whose panel was given none", () => {
  mount();

  expect(screen.queryByRole("img")).toBeNull();
});

it("tints the poster's tile for the file's workflow, and for a workflow that is gone", () => {
  mount({ poster: { url: null, workflow: "TV" } });
  expect(
    (
      screen.getByRole("dialog").querySelector(".mm-tile") as HTMLElement
    ).style.getPropertyValue("--tile-hue"),
  ).toBe("265");
  cleanup();

  mount({ poster: { url: null, workflow: "Films" } });
  expect(
    (
      screen.getByRole("dialog").querySelector(".mm-tile") as HTMLElement
    ).style.getPropertyValue("--tile-hue"),
  ).toBe("205");
});

it("says where a file is now, with how far through it is, instead of saying nothing has happened", () => {
  mount({
    log: log({ entries: [] }),
    now: {
      stage: "Processing",
      status: "39% · 16 s left",
      working: true,
      progress: 39,
      facts: ["Speed 479× real time", "Removing 4 audio, 2 subtitles"],
    },
  });

  const now = screen.getByRole("region", { name: "Right now" });
  expect(now).toHaveTextContent("Processing");
  expect(now).toHaveTextContent("39% · 16 s left");
  expect(within(now).getByRole("progressbar")).toHaveAttribute(
    "aria-valuenow",
    "39",
  );
  expect(now).toHaveTextContent("Speed 479× real time");
  expect(screen.queryByText(/Nothing yet/)).not.toBeInTheDocument();
  expect(screen.getByText(/shows here once it has finished/)).toBeVisible();
});

it("leaves the bar out for a file that is only waiting", () => {
  mount({
    log: log({ entries: [] }),
    now: {
      stage: "Queued",
      status: "Waiting its turn",
      working: false,
      progress: null,
      facts: [],
    },
  });

  expect(screen.queryByRole("progressbar")).not.toBeInTheDocument();
});

it("says nothing has happened to a file that is neither on the Pipeline nor has any activity", () => {
  mount({ log: log({ entries: [] }) });

  expect(screen.getByText(/Nothing yet/)).toBeVisible();
  expect(
    screen.queryByRole("region", { name: "Right now" }),
  ).not.toBeInTheDocument();
});
