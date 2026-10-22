/**
 * Issue #498: a client-side mirror of `Weir.Core.Rules.TrackNaming` (`ValidateTemplate` and
 * `Render`) so the rule set editor can validate a track name template and show a live preview
 * against a sample track without a round trip to the server. The web preview is illustrative
 * only — the server (`TrackNaming.ValidateAll`) is still the source of truth for saving.
 */

export const DEFAULT_TRACK_NAME_TEMPLATE =
  "{language}{variant} {channels} {codec}";

const KNOWN_PLACEHOLDERS = [
  "language",
  "variant",
  "channels",
  "codec",
  "flags",
];
const PLACEHOLDER_PATTERN = /\{(\w*)\}/g;

export interface TrackNamePreviewFlags {
  forced?: boolean;
  hearingImpaired?: boolean;
  commentary?: boolean;
  audioDescription?: boolean;
}

export interface TrackNamePreviewSample {
  /** e.g. "English". */
  language: string;
  /** e.g. "VFQ", or omitted/empty for no detected variant. */
  variant?: string | null;
  /** Total channel count, e.g. 6 for 5.1. */
  channels: number;
  /** Already a display name, e.g. "TrueHD". */
  codec: string;
  flags?: TrackNamePreviewFlags;
}

/** The sample track shown in the editor's live preview. */
export const SAMPLE_TRACK: TrackNamePreviewSample = {
  language: "English",
  variant: "VFQ",
  channels: 6,
  codec: "TrueHD",
};

/**
 * The first unknown placeholder's message (matching `TrackNameTemplateException`'s wording), or
 * `null` when every placeholder in `template` is one of `{language}` `{variant}` `{channels}`
 * `{codec}` `{flags}`.
 */
export function trackNameTemplateError(template: string): string | null {
  for (const match of template.matchAll(PLACEHOLDER_PATTERN)) {
    const name = match[1];
    if (!KNOWN_PLACEHOLDERS.includes(name)) {
      return (
        `Unknown placeholder '{${name}}' in track name template. Supported placeholders: ` +
        `${KNOWN_PLACEHOLDERS.map((p) => `{${p}}`).join(", ")}.`
      );
    }
  }
  return null;
}

function formatChannels(channels: number): string {
  if (channels <= 0) return "";
  if (channels === 1) return "1.0";
  if (channels === 2) return "2.0";
  return `${channels - 1}.1`;
}

function formatFlags(flags: TrackNamePreviewFlags | undefined): string {
  if (!flags) return "";
  const parts: string[] = [];
  if (flags.forced) parts.push("Forced");
  if (flags.hearingImpaired) parts.push("Hearing Impaired");
  if (flags.commentary) parts.push("Commentary");
  if (flags.audioDescription) parts.push("Audio Description");
  return parts.join(", ");
}

/**
 * Renders `template` against `sample`, or `null` when the template has an unknown placeholder
 * (call `trackNameTemplateError` first to show why).
 */
export function previewTrackName(
  template: string,
  sample: TrackNamePreviewSample = SAMPLE_TRACK,
): string | null {
  if (trackNameTemplateError(template) !== null) {
    return null;
  }

  const rendered = template.replace(
    PLACEHOLDER_PATTERN,
    (_whole, name: string) => {
      switch (name) {
        case "language":
          return sample.language;
        case "variant":
          return sample.variant ? ` (${sample.variant})` : "";
        case "channels":
          return formatChannels(sample.channels);
        case "codec":
          return sample.codec;
        // "flags": the template check above has already refused every other name.
        default:
          return formatFlags(sample.flags);
      }
    },
  );

  return rendered.replace(/\s+/g, " ").trim();
}
