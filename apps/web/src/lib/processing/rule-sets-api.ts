import { apiFetch, readJson, requireOk } from "../api/client";
import { sendJson } from "../api/send-json";
import type { Schema } from "../api/types";

/** Issue #498: per-flag track name templates, checked forced, then hearing-impaired, then commentary, then audio description. */
export type ProcessingTrackNameOverrides = Schema<"TrackNameOverrides">;

export interface ProcessingRuleSet {
  id: number;
  name: string;
  primary_audio_lang: string;
  secondary_audio_lang: string;
  tertiary_audio_lang: string;
  default_audio_slot: string;
  remove_commentary: boolean;
  subtitle_mode: string;
  subtitle_langs_csv: string;
  preserve_forced_subs: boolean;
  preserve_default_subs: boolean;
  audio_preference_mode: string;
  /** Ordered track sorters as JSON. Empty means the default order Processing has always applied. */
  audio_sorters_json: string;
  subtitle_sorters_json: string;
  /** Keep the audio in the film's original language. Needs a metadata provider; without one the preferences decide. */
  keep_original_language: boolean;
  original_language_additional_csv: string;
  original_language_keep_only_first: boolean;
  original_language_first_if_none: boolean;
  original_language_treat_empty_as_original: boolean;
  /** An embedded poster is carried as a video stream, so this removes a stream as well as an image. */
  remove_images: boolean;
  remove_attachments: boolean;
  remove_title: boolean;
  remove_language_tags: boolean;
  remove_other_metadata: boolean;
  /** Issue #495: drop a subtitle track detected as hearing-impaired (SDH/CC), from its flag or its name. Off by default. */
  remove_hearing_impaired_subs: boolean;
  /** Issue #497: "single" (today's behaviour) or "per_language" — keep the best track of each configured audio language. */
  audio_keep_mode: string;
  /** Issue #497: 0 (default) means unlimited; otherwise the most subtitle tracks kept per language. */
  subtitle_max_per_language: number;
  /** Issue #497: "text_first" (default), "image_first" or "accessibility" — how the subtitle cap picks a winner. */
  subtitle_quality_strategy: string;
  /** Issue #498: write a standard name on every kept audio/subtitle track, from a template. Off by default. */
  standardize_track_names: boolean;
  /** Issue #498: placeholders {language} {variant} {channels} {codec} {flags}. */
  track_name_template: string;
  track_name_overrides: ProcessingTrackNameOverrides;
  /** Issue #498: clear scene-tag video track names (e.g. "x265-GROUP"). Off by default. */
  clear_video_track_names: boolean;
  /** Issue #498: drop the container's chapter list. Off by default. */
  remove_chapters: boolean;
  /** Libraries pointing at this rule set. Deleting one still in use is refused. */
  used_by_library_count: number;
  /** The background re-plans queued for libraries using this profile. Sent only when a profile is saved. */
  library_rescan_job_ids?: number[];
  updated_at: string | null;
}

export type ProcessingRuleSetWrite = Omit<
  ProcessingRuleSet,
  "id" | "used_by_library_count" | "library_rescan_job_ids" | "updated_at"
>;

/** Strip server-owned fields before a rule set is sent back to the API. */
export function writeFromProcessingRuleSet(
  value: ProcessingRuleSet | ProcessingRuleSetWrite,
): ProcessingRuleSetWrite {
  return {
    name: value.name,
    primary_audio_lang: value.primary_audio_lang,
    secondary_audio_lang: value.secondary_audio_lang,
    tertiary_audio_lang: value.tertiary_audio_lang,
    default_audio_slot: value.default_audio_slot,
    remove_commentary: value.remove_commentary,
    subtitle_mode: value.subtitle_mode,
    subtitle_langs_csv: value.subtitle_langs_csv,
    preserve_forced_subs: value.preserve_forced_subs,
    preserve_default_subs: value.preserve_default_subs,
    audio_preference_mode: value.audio_preference_mode,
    audio_sorters_json: value.audio_sorters_json,
    subtitle_sorters_json: value.subtitle_sorters_json,
    keep_original_language: value.keep_original_language,
    original_language_additional_csv: value.original_language_additional_csv,
    original_language_keep_only_first: value.original_language_keep_only_first,
    original_language_first_if_none: value.original_language_first_if_none,
    original_language_treat_empty_as_original:
      value.original_language_treat_empty_as_original,
    remove_images: value.remove_images,
    remove_attachments: value.remove_attachments,
    remove_title: value.remove_title,
    remove_language_tags: value.remove_language_tags,
    remove_other_metadata: value.remove_other_metadata,
    remove_hearing_impaired_subs: value.remove_hearing_impaired_subs,
    audio_keep_mode: value.audio_keep_mode,
    subtitle_max_per_language: value.subtitle_max_per_language,
    subtitle_quality_strategy: value.subtitle_quality_strategy,
    standardize_track_names: value.standardize_track_names,
    track_name_template: value.track_name_template,
    track_name_overrides: { ...value.track_name_overrides },
    clear_video_track_names: value.clear_video_track_names,
    remove_chapters: value.remove_chapters,
  };
}

const processingRuleSetsPath = () => "/api/v1/processing/rule-sets";

export async function fetchProcessingRuleSets(): Promise<ProcessingRuleSet[]> {
  const path = processingRuleSetsPath();
  const response = await apiFetch(path);
  await requireOk(path, response, "Could not load rule sets");
  return readJson<ProcessingRuleSet[]>(response);
}

export async function createProcessingRuleSet(
  data: ProcessingRuleSetWrite,
): Promise<ProcessingRuleSet> {
  const path = processingRuleSetsPath();
  const response = await sendJson(
    path,
    "POST",
    data,
    "Could not add that rule set",
  );
  return readJson<ProcessingRuleSet>(response);
}

export async function updateProcessingRuleSet(
  id: number,
  data: ProcessingRuleSetWrite,
): Promise<ProcessingRuleSet> {
  const path = `${processingRuleSetsPath()}/${id}`;
  const response = await sendJson(
    path,
    "PUT",
    data,
    "Could not save that rule set",
  );
  return readJson<ProcessingRuleSet>(response);
}

export async function deleteProcessingRuleSet(id: number): Promise<void> {
  const path = `${processingRuleSetsPath()}/${id}`;
  await sendJson(path, "DELETE", {}, "Could not remove that rule set");
}
