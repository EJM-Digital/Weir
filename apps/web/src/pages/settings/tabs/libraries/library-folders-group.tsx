import { QuietDisclosure } from "../../../../components/shared/quiet-section";
import type { ProcessingRuleSet } from "../../../../lib/processing/rule-sets-api";
import {
  MEDIA_TYPE_OPTIONS,
  WRITER_HINTS,
  WRITER_OPTIONS,
} from "./library-options";
import {
  FolderSetting,
  SelectSetting,
  TextSetting,
  type LibraryFormBinding,
  type SettingOption,
} from "./library-settings";

/** A library left on "" gets its kind's default profile, That choice is always offered first, so a profile once chosen can be undone. */
function ruleSetOptions(
  binding: LibraryFormBinding,
  ruleSets: ProcessingRuleSet[],
): SettingOption[] {
  const { form } = binding;
  return [
    {
      value: "",
      label: `${form.media_type === "tv" ? "TV default" : "Movies default"} (the default for its kind)`,
    },
    ...ruleSets.map((ruleSet) => ({
      value: String(ruleSet.id),
      label: ruleSet.name,
    })),
  ];
}

/** What to say under a watched or output folder that a media manager owns. */
function syncedNote(manager: string, folder: string): string {
  return folder.trim() === ""
    ? `${manager} hasn't said yet; set it in ${manager} and Weir will fill it in.`
    : `From ${manager}; change it in ${manager}.`;
}

/** What the library is called, what it holds and which folders it uses. */
export function LibraryFoldersGroup({
  binding,
  ruleSets,
  syncedFrom,
}: {
  binding: LibraryFormBinding;
  ruleSets: ProcessingRuleSet[];
  /** The media manager that owns the watched and output folders, when one does; they are then shown, not edited. */
  syncedFrom?: string;
}) {
  const { form } = binding;
  return (
    <QuietDisclosure
      title="Identity and folders"
      detail="One watched folder, one safe work area, and one finished output."
      defaultOpen
    >
      <div className="mm-editor-grid">
        <TextSetting
          binding={binding}
          name="name"
          label="Name"
          width="medium"
          placeholder="Movies 4K"
        />
        <SelectSetting
          binding={binding}
          name="media_type"
          label="Media type"
          options={MEDIA_TYPE_OPTIONS}
        />
        <SelectSetting
          binding={binding}
          name="rule_set_id"
          label="Rules profile"
          options={ruleSetOptions(binding, ruleSets)}
          hint="Create and edit profiles under Rules."
        />
        <SelectSetting
          binding={binding}
          name="remux_writer"
          label="Writes files with"
          options={WRITER_OPTIONS}
          hint={WRITER_HINTS[binding.form.remux_writer]}
          testId="library-writer-choice"
        />
        <FolderSetting
          binding={binding}
          name="watched_folder"
          label="Watched folder"
          placeholder="/srv/media/movies-4k"
          lockedNote={
            syncedFrom ? syncedNote(syncedFrom, form.watched_folder) : undefined
          }
        />
        <FolderSetting
          binding={binding}
          name="output_folder"
          label="Output folder"
          placeholder="/srv/media/movies-4k-out"
          lockedNote={
            syncedFrom ? syncedNote(syncedFrom, form.output_folder) : undefined
          }
        />
        <FolderSetting
          binding={binding}
          name="work_folder"
          label="Work folder"
          hint="Leave empty to use Weir's private temporary folder; put it on the same volume as the output folder so finished files move instead of copying."
        />
      </div>
    </QuietDisclosure>
  );
}
