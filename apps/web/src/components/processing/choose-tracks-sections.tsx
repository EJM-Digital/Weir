import { Fragment, type ReactNode } from "react";

import type { ProcessingFileTrack } from "../../lib/processing/files-api";
import { plural } from "../../lib/ui/mm-plural";
import { useTableColumns } from "../../lib/ui/use-table-columns";
import { ColumnsMenu } from "../shared/columns-menu";
import { SortableColumnHeader } from "../shared/sortable-column-header";
import {
  TRACK_COLUMN_LABELS,
  TRACK_COLUMNS,
  type TrackColumnId,
} from "./choose-tracks-columns";
import type { TrackChoice } from "./use-track-choice";

function TrackRow({
  track,
  choice,
  order,
}: {
  track: ProcessingFileTrack;
  choice: TrackChoice;
  order: readonly TrackColumnId[];
}) {
  const state = choice.rows[track.index];
  const kept = state?.keep ?? false;
  const label = choice.labelOf(track);
  const type = track.type;
  const cells: Record<TrackColumnId, ReactNode> = {
    keep: (
      <td data-col="keep" data-label={TRACK_COLUMN_LABELS.keep}>
        {/* The label is the target: a 24px square around a box drawn at the text's own size. */}
        <label className="mm-tracks-check-target">
          <input
            type="checkbox"
            checked={kept}
            onChange={(e) => choice.setKeep(track, e.target.checked)}
            aria-label={`Keep ${label}`}
            data-testid={`choose-tracks-keep-${track.index}`}
          />
        </label>
      </td>
    ),
    track: (
      <td
        data-col="track"
        data-label={TRACK_COLUMN_LABELS.track}
        className="mm-tracks-table__track"
      >
        {label}
      </td>
    ),
    default: (
      <td data-col="default" data-label={TRACK_COLUMN_LABELS.default}>
        {type === "audio" || type === "subtitle" ? (
          <label className="mm-tracks-check-target">
            <input
              type="radio"
              name={`choose-tracks-default-${type}`}
              checked={choice.isDefault(track)}
              disabled={!kept}
              onChange={() => choice.setDefault(type, track.index)}
              aria-label={`Make ${label} the default ${type} track`}
              data-testid={`choose-tracks-default-${track.index}`}
            />
          </label>
        ) : null}
      </td>
    ),
    forced: (
      <td data-col="forced" data-label={TRACK_COLUMN_LABELS.forced}>
        {track.type === "subtitle" ? (
          <label className="mm-tracks-check-target">
            <input
              type="checkbox"
              checked={state?.forced ?? false}
              disabled={!kept}
              onChange={(e) => choice.setForced(track.index, e.target.checked)}
              aria-label={`Mark ${label} forced`}
              data-testid={`choose-tracks-forced-${track.index}`}
            />
          </label>
        ) : null}
      </td>
    ),
    rule: (
      <td
        data-col="rule"
        data-label={TRACK_COLUMN_LABELS.rule}
        className="mm-tracks-table__rule"
      >
        <strong>{track.rule_would_keep ? "Would keep" : "Would remove"}</strong>{" "}
        — {track.rule_reason}
      </td>
    ),
  };
  return (
    <tr data-testid={`choose-tracks-row-${track.index}`}>
      {order.map((id) => (
        <Fragment key={id}>{cells[id]}</Fragment>
      ))}
    </tr>
  );
}

/** Choosing one subtitle as the default can be undone: this is the choice of none. */
function NoDefaultSubtitle({ choice }: { choice: TrackChoice }) {
  if (!choice.selectable.some((track) => track.type === "subtitle")) {
    return null;
  }
  return (
    <label className="mm-tracks-no-default">
      <input
        type="radio"
        name="choose-tracks-default-subtitle"
        checked={!choice.hasDefaultSubtitle}
        onChange={() => choice.setDefault("subtitle", null)}
        data-testid="choose-tracks-default-none"
      />
      No default subtitle
    </label>
  );
}

/**
 * Every video, audio and subtitle track with its keep, default and forced choices. On a narrow screen each
 * row stacks into a card of labelled values rather than scrolling sideways.
 */
export function TrackTable({ choice }: { choice: TrackChoice }) {
  const columns = useTableColumns(TRACK_COLUMNS);
  return (
    <div className="mm-tracks-table-wrap">
      <div className="mm-panel__aside px-3 py-2">
        <ColumnsMenu table={columns} />
      </div>
      <table className="mm-tracks-table" {...columns.tableProps}>
        <thead>
          <tr>
            {columns.order.map((id) => (
              <SortableColumnHeader key={id} heading={columns.heading(id)} />
            ))}
          </tr>
        </thead>
        <tbody>
          {choice.selectable.map((track) => (
            <TrackRow
              key={track.index}
              track={track}
              choice={choice}
              order={columns.order}
            />
          ))}
        </tbody>
      </table>
      <NoDefaultSubtitle choice={choice} />
    </div>
  );
}

const OTHER_KINDS: Record<string, string> = {
  image: "Cover image",
  attachment: "Attachment",
};

/** Images and attachments follow the saved rules, so they are listed but not offered. */
export function OtherStreams({ tracks }: { tracks: ProcessingFileTrack[] }) {
  if (tracks.length === 0) return null;
  return (
    <details className="mm-story-pass__detail">
      <summary>
        {plural(
          tracks.length,
          "embedded image or attachment",
          "embedded images or attachments",
        )}{" "}
        not shown above — the saved rules decide these, not this choice
      </summary>
      <ul>
        {tracks.map((track) => (
          <li key={track.index}>
            {OTHER_KINDS[track.type] ?? "Other stream"}
            {track.title ? ` “${track.title}”` : ""} —{" "}
            {track.rule_would_keep ? "kept" : "removed"}: {track.rule_reason}
          </li>
        ))}
      </ul>
    </details>
  );
}

/**
 * The kept tracks in output order. Plain up and down buttons rather than drag and drop, so it works
 * the same with a keyboard, a screen reader or a mouse.
 */
export function TrackOrder({ choice }: { choice: TrackChoice }) {
  const kept = choice.keptTracks;
  return (
    <section className="mm-tracks-order" aria-label="Track order">
      <h3 className="mm-story-pass__when">Order</h3>
      {kept.length === 0 ? (
        <p className="mm-story-panel__note">Nothing is kept yet.</p>
      ) : (
        <ol className="mm-tracks-order__list">
          {kept.map((track, position) => (
            <li
              key={track.index}
              data-testid={`choose-tracks-order-${track.index}`}
            >
              <span>{choice.labelOf(track)}</span>
              <span className="mm-tracks-order__buttons">
                <button
                  type="button"
                  onClick={() => choice.move(track.index, -1)}
                  disabled={position === 0}
                  aria-label={`Move ${choice.labelOf(track)} earlier`}
                  data-testid={`choose-tracks-move-up-${track.index}`}
                >
                  ↑
                </button>
                <button
                  type="button"
                  onClick={() => choice.move(track.index, 1)}
                  disabled={position === kept.length - 1}
                  aria-label={`Move ${choice.labelOf(track)} later`}
                  data-testid={`choose-tracks-move-down-${track.index}`}
                >
                  ↓
                </button>
              </span>
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}

export function DroppedTracks({ choice }: { choice: TrackChoice }) {
  const tracks = choice.dropped;
  return (
    <section
      className="mm-tracks-dropped"
      aria-label="Tracks that will be dropped"
    >
      <h3 className="mm-story-pass__when">Will be dropped</h3>
      {tracks.length === 0 ? (
        <p className="mm-story-panel__note">
          Nothing — every video, audio and subtitle track is kept.
        </p>
      ) : (
        <ul
          className="mm-tracks-order__list"
          data-testid="choose-tracks-dropped-list"
        >
          {tracks.map((track) => (
            <li key={track.index}>{choice.labelOf(track)}</li>
          ))}
        </ul>
      )}
    </section>
  );
}
