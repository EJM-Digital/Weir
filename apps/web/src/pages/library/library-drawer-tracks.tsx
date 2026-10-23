/** The file panel's track lists: what the rules would keep and remove, or your own choice while you make one. */
import { Chip } from "../../components/panels/chip";
import { describeTrackIn, positionsByKind } from "../../lib/format/track";
import type { ProcessingRulesPreviewTrack } from "../../lib/processing/rules-preview-api";
import { trackMeaning } from "../../lib/ui/track-meaning";

export const rulesKeep = (track: ProcessingRulesPreviewTrack): boolean =>
  track.action === "keep";

export function toggled(kept: Set<number>, index: number): Set<number> {
  const next = new Set(kept);
  if (next.has(index)) next.delete(index);
  else next.add(index);
  return next;
}

export function TrackList({
  title,
  tracks,
  positions,
  kept,
  onToggle,
}: {
  title: string;
  tracks: ProcessingRulesPreviewTrack[];
  /** Every track's place among its own kind in the file, from positionsByKind. */
  positions: ReturnType<typeof positionsByKind>;
  /** The tracks you have chosen to keep, or null while the rules are deciding. */
  kept: Set<number> | null;
  onToggle: (index: number) => void;
}): React.ReactElement | null {
  if (tracks.length === 0) return null;
  return (
    <div className="mm-drawer__tracks">
      <p className="mm-drawer__eyebrow">{title}</p>
      {tracks.map((track) => {
        const keeping = kept ? kept.has(track.index) : rulesKeep(track);
        const row = (
          <>
            <Chip meaning={trackMeaning(keeping)} dot={false}>
              {keeping ? "Keep" : "Remove"}
            </Chip>
            <span className="mm-drawer__track-name">
              {describeTrackIn(track, positions)}
            </span>
            <span className="mm-drawer__track-why">
              {kept ? "" : (track.reasons[0] ?? "")}
            </span>
          </>
        );
        return kept ? (
          <label
            key={track.index}
            className="mm-drawer__track mm-drawer__track--choosable"
          >
            <input
              type="checkbox"
              checked={keeping}
              onChange={() => onToggle(track.index)}
            />
            {row}
          </label>
        ) : (
          <div key={track.index} className="mm-drawer__track">
            {row}
          </div>
        );
      })}
    </div>
  );
}
