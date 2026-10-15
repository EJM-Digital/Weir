import { useEffect, useState } from "react";

import { describeTrack, positionsByKind } from "../../lib/format/track";
import type {
  ProcessingFileTrack,
  ProcessingFileTracks,
  ProcessingManualPlanChoice,
} from "../../lib/processing/files-api";

type SelectableType = "video" | "audio" | "subtitle";

function isSelectable(
  type: ProcessingFileTrack["type"],
): type is SelectableType {
  return type === "video" || type === "audio" || type === "subtitle";
}

type RowState = {
  keep: boolean;
  default: boolean;
  forced: boolean;
};

/** Seeded from what the saved rules would have done, so the starting point is never empty. */
function initialRowState(track: ProcessingFileTrack): RowState {
  return {
    keep: track.rule_would_keep,
    default: track.rule_would_keep && track.default,
    forced: track.rule_would_keep && track.forced,
  };
}

function validationOf(kept: ProcessingFileTrack[]): string | null {
  if (!kept.some((track) => track.type === "video")) {
    return "Keep at least one video track.";
  }
  if (!kept.some((track) => track.type === "audio")) {
    return "Keep at least one audio track.";
  }
  return null;
}

/**
 * Which tracks to keep, their default and forced flags, and their order. Reset from a fresh set of
 * tracks: a new open, or a reload after "choose again".
 */
export function useTrackChoice(tracks: ProcessingFileTracks | undefined) {
  const [rows, setRows] = useState<Record<number, RowState>>({});
  const [order, setOrder] = useState<number[]>([]);

  useEffect(() => {
    if (!tracks) return;
    const nextRows: Record<number, RowState> = {};
    const kept: number[] = [];
    for (const track of tracks.streams) {
      if (!isSelectable(track.type)) continue;
      const state = initialRowState(track);
      nextRows[track.index] = state;
      if (state.keep) kept.push(track.index);
    }
    setRows(nextRows);
    setOrder(kept);
  }, [tracks]);

  const streams = tracks?.streams ?? [];
  const positions = positionsByKind(streams);
  const selectable = streams.filter((track) => isSelectable(track.type));
  const other = streams.filter((track) => !isSelectable(track.type));
  const keptTracks = order
    .map((index) => selectable.find((track) => track.index === index))
    .filter((track): track is ProcessingFileTrack => track !== undefined);

  const setKeep = (track: ProcessingFileTrack, keep: boolean) => {
    setRows((previous) => ({
      ...previous,
      [track.index]: { ...previous[track.index], keep },
    }));
    setOrder((previous) => {
      if (!keep) return previous.filter((index) => index !== track.index);
      return previous.includes(track.index)
        ? previous
        : [...previous, track.index];
    });
  };

  /** Makes `index` the one default of its kind; null leaves that kind with no default at all. */
  const setDefault = (type: SelectableType, index: number | null) => {
    setRows((previous) => {
      const next = { ...previous };
      for (const track of selectable) {
        if (track.type !== type) continue;
        next[track.index] = {
          ...next[track.index],
          default: track.index === index,
        };
      }
      return next;
    });
  };

  const setForced = (index: number, forced: boolean) => {
    setRows((previous) => ({
      ...previous,
      [index]: { ...previous[index], forced },
    }));
  };

  const move = (index: number, direction: -1 | 1) => {
    setOrder((previous) => {
      const position = previous.indexOf(index);
      const target = position + direction;
      if (position < 0 || target < 0 || target >= previous.length) {
        return previous;
      }
      const next = [...previous];
      [next[position], next[target]] = [next[target], next[position]];
      return next;
    });
  };

  // The default audio track is the kept one marked default, else the first kept in the output order, as the automatic
  // rules do: a file always has one, so dropping the default track hands it to the next. A subtitle track is default
  // only when chosen, and only while it is kept.
  const keptAudio = keptTracks.filter((track) => track.type === "audio");
  const defaultAudioIndex = (
    keptAudio.find((track) => rows[track.index]?.default) ?? keptAudio[0]
  )?.index;
  const isDefault = (track: ProcessingFileTrack) =>
    track.type === "audio"
      ? track.index === defaultAudioIndex
      : track.type === "subtitle" &&
        (rows[track.index]?.keep ?? false) &&
        (rows[track.index]?.default ?? false);

  // Default and forced only mean anything for audio and subtitle tracks; a video track always
  // submits false for both, whatever its own disposition on the source said.
  const choice = (): ProcessingManualPlanChoice => ({
    keep: keptTracks.map((track) => ({
      index: track.index,
      default: isDefault(track),
      forced:
        track.type === "video" ? false : (rows[track.index]?.forced ?? false),
    })),
    order,
  });

  return {
    rows,
    /** "Audio 2 · Japanese · 2.0 AAC": the track in words, numbered among its own kind. */
    labelOf: (track: ProcessingFileTrack) =>
      describeTrack(track, positions.get(track.index) ?? 1),
    /** Whether the track is the default of its kind in what would be submitted. */
    isDefault,
    /** Whether a kept subtitle is marked default; with none, players show no subtitles until asked. */
    hasDefaultSubtitle: selectable.some(
      (track) => track.type === "subtitle" && isDefault(track),
    ),
    selectable,
    other,
    keptTracks,
    dropped: selectable.filter((track) => !(rows[track.index]?.keep ?? false)),
    validationError: validationOf(keptTracks),
    setKeep,
    setDefault,
    setForced,
    move,
    choice,
  };
}

export type TrackChoice = ReturnType<typeof useTrackChoice>;
