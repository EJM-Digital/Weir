import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type {
  ProcessingFileTracks,
  ProcessingManualPlanChoice,
} from "../../lib/processing/files-api";
import { heldFileTracks } from "../../test/track-fixtures";
import { ChooseTracksPanel } from "./choose-tracks-panel";

const onSubmit = vi.fn<(choice: ProcessingManualPlanChoice) => void>();

const ENGLISH_AUDIO = 1;
const JAPANESE_AUDIO = 2;
const ENGLISH_SUBTITLE = 3;
const SPANISH_SUBTITLE = 4;

function open(tracks: ProcessingFileTracks = heldFileTracks()) {
  render(
    <ChooseTracksPanel
      open
      fileName="Film (2024)/film.mkv"
      tracks={tracks}
      loading={false}
      loadError={null}
      submitting={false}
      submitError={null}
      onClose={vi.fn()}
      onSubmit={onSubmit}
    />,
  );
}

const control = (name: string, index: number) =>
  screen.getByTestId(`choose-tracks-${name}-${index}`);
const noDefaultSubtitle = () =>
  screen.getByTestId("choose-tracks-default-none");

function submitted(): ProcessingManualPlanChoice {
  fireEvent.click(screen.getByTestId("choose-tracks-submit"));
  return onSubmit.mock.calls.at(-1)![0];
}

const flagsOf = (choice: ProcessingManualPlanChoice, index: number) =>
  choice.keep.find((track) => track.index === index);

beforeEach(() => {
  onSubmit.mockReset();
});

describe("Choose tracks: forced subtitles", () => {
  it("offers Forced on subtitle rows only", () => {
    open();

    expect(screen.queryByTestId("choose-tracks-forced-0")).toBeNull();
    expect(
      screen.queryByTestId(`choose-tracks-forced-${ENGLISH_AUDIO}`),
    ).toBeNull();
    expect(control("forced", ENGLISH_SUBTITLE)).toBeInTheDocument();
    expect(control("forced", SPANISH_SUBTITLE)).toBeInTheDocument();
  });

  it("is unavailable for a subtitle that is not kept, and available once it is", () => {
    open();

    expect(control("forced", SPANISH_SUBTITLE)).toBeDisabled();

    fireEvent.click(control("keep", SPANISH_SUBTITLE));

    expect(control("forced", SPANISH_SUBTITLE)).toBeEnabled();
  });

  it("reaches the submitted choice for the subtitle it was ticked on, and for no other", () => {
    open();

    fireEvent.click(control("forced", ENGLISH_SUBTITLE));
    const choice = submitted();

    expect(flagsOf(choice, ENGLISH_SUBTITLE)).toEqual({
      index: ENGLISH_SUBTITLE,
      default: false,
      forced: true,
    });
    expect(flagsOf(choice, ENGLISH_AUDIO)?.forced).toBe(false);
  });

  it("is taken back out of the submitted choice when unticked", () => {
    open();

    fireEvent.click(control("forced", ENGLISH_SUBTITLE));
    fireEvent.click(control("forced", ENGLISH_SUBTITLE));

    expect(flagsOf(submitted(), ENGLISH_SUBTITLE)?.forced).toBe(false);
  });

  it("stays on a subtitle through making another the default", () => {
    open();
    fireEvent.click(control("keep", SPANISH_SUBTITLE));

    fireEvent.click(control("forced", ENGLISH_SUBTITLE));
    fireEvent.click(control("default", SPANISH_SUBTITLE));
    const choice = submitted();

    expect(flagsOf(choice, ENGLISH_SUBTITLE)).toMatchObject({
      default: false,
      forced: true,
    });
    expect(flagsOf(choice, SPANISH_SUBTITLE)).toMatchObject({
      default: true,
      forced: false,
    });
  });
});

describe("Choose tracks: the default subtitle", () => {
  it("starts with no default subtitle, as the file has none, and says so", () => {
    open();

    expect(noDefaultSubtitle()).toBeChecked();
    expect(control("default", ENGLISH_SUBTITLE)).not.toBeChecked();
    expect(flagsOf(submitted(), ENGLISH_SUBTITLE)?.default).toBe(false);
  });

  it("makes the chosen subtitle the one default of the submitted choice", () => {
    open();
    fireEvent.click(control("keep", SPANISH_SUBTITLE));

    fireEvent.click(control("default", ENGLISH_SUBTITLE));
    fireEvent.click(control("default", SPANISH_SUBTITLE));
    const choice = submitted();

    expect(control("default", SPANISH_SUBTITLE)).toBeChecked();
    expect(control("default", ENGLISH_SUBTITLE)).not.toBeChecked();
    expect(flagsOf(choice, SPANISH_SUBTITLE)?.default).toBe(true);
    expect(flagsOf(choice, ENGLISH_SUBTITLE)?.default).toBe(false);
  });

  it("is no longer the case that there is none once a subtitle is the default", () => {
    open();

    fireEvent.click(control("default", ENGLISH_SUBTITLE));

    expect(noDefaultSubtitle()).not.toBeChecked();
  });

  it("goes back to none with No default subtitle, and no subtitle is submitted as the default", () => {
    open();
    fireEvent.click(control("default", ENGLISH_SUBTITLE));

    fireEvent.click(noDefaultSubtitle());
    const choice = submitted();

    expect(noDefaultSubtitle()).toBeChecked();
    expect(control("default", ENGLISH_SUBTITLE)).not.toBeChecked();
    expect(choice.keep.filter((track) => track.default)).toEqual([
      expect.objectContaining({ index: ENGLISH_AUDIO }),
    ]);
  });

  it("leaves the default audio track alone when the subtitle default changes", () => {
    open();

    fireEvent.click(control("default", ENGLISH_SUBTITLE));
    fireEvent.click(noDefaultSubtitle());

    expect(control("default", ENGLISH_AUDIO)).toBeChecked();
    expect(flagsOf(submitted(), ENGLISH_AUDIO)?.default).toBe(true);
  });

  it("counts a default subtitle that is then dropped as none", () => {
    open();
    fireEvent.click(control("default", ENGLISH_SUBTITLE));

    fireEvent.click(control("keep", ENGLISH_SUBTITLE));

    expect(noDefaultSubtitle()).toBeChecked();
  });

  it("is not offered for a file with no subtitles", () => {
    const tracks = heldFileTracks();
    open({
      ...tracks,
      streams: tracks.streams.filter((stream) => stream.type !== "subtitle"),
    });

    expect(screen.queryByTestId("choose-tracks-default-none")).toBeNull();
  });
});

describe("Choose tracks: the order of the kept tracks", () => {
  const orderOf = () =>
    screen
      .getAllByTestId(/^choose-tracks-order-\d+$/)
      .map((row) => Number(row.dataset.testid?.split("-").at(-1)));

  it("moves a track later with its down button and submits the new order", () => {
    open();
    expect(orderOf()).toEqual([0, ENGLISH_AUDIO, ENGLISH_SUBTITLE]);

    fireEvent.click(
      screen.getByTestId(`choose-tracks-move-down-${ENGLISH_AUDIO}`),
    );

    expect(orderOf()).toEqual([0, ENGLISH_SUBTITLE, ENGLISH_AUDIO]);
    expect(submitted().order).toEqual([0, ENGLISH_SUBTITLE, ENGLISH_AUDIO]);
  });

  it("cannot move the last track later or the first track earlier", () => {
    open();

    expect(
      screen.getByTestId(`choose-tracks-move-down-${ENGLISH_SUBTITLE}`),
    ).toBeDisabled();
    expect(screen.getByTestId("choose-tracks-move-up-0")).toBeDisabled();
    expect(screen.getByTestId("choose-tracks-move-down-0")).toBeEnabled();
  });

  it("puts a track kept later at the end of the order", () => {
    open();

    fireEvent.click(control("keep", JAPANESE_AUDIO));

    expect(orderOf()).toEqual([
      0,
      ENGLISH_AUDIO,
      ENGLISH_SUBTITLE,
      JAPANESE_AUDIO,
    ]);
    expect(submitted().order).toEqual([
      0,
      ENGLISH_AUDIO,
      ENGLISH_SUBTITLE,
      JAPANESE_AUDIO,
    ]);
  });
});
