import { useEffect, useState } from "react";

import { PageLoading } from "../../../../components/shared/page-loading";
import { QuietSection } from "../../../../components/shared/quiet-section";
import { PageToolbarAddButton } from "../../../../components/shell/page-toolbar-actions";
import { ConfirmDialog } from "../../../../components/ui/confirm-dialog";
import { errorMessage } from "../../../../lib/api/error-message";
import { canEdit } from "../../../../lib/auth/can-edit";
import { useMeQuery } from "../../../../lib/auth/queries";
import {
  writeFromProcessingRuleSet,
  type ProcessingRuleSet,
  type ProcessingRuleSetWrite,
} from "../../../../lib/processing/rule-sets-api";
import {
  useCreateProcessingRuleSet,
  useDeleteProcessingRuleSet,
  useProcessingLibrariesQuery,
  useProcessingRuleSetsQuery,
  useUpdateProcessingRuleSet,
} from "../../../../lib/processing/libraries-queries";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { plural } from "../../../../lib/ui/mm-plural";
import { SaveModelNote } from "../../save-model-note";
import { SettingsLoadError } from "../../settings-load-error";
import { useLeaveConfirmation, useUnsavedChanges } from "../../unsaved-changes";
import { ProfileBar } from "./profile-bar";
import { ProfileRules } from "./profile-rules";
import type { RuleSetBinding } from "./rule-set-fields";
import { EMPTY_RULE_SET, sameRuleSet } from "./rule-set-model";

/** What the new-profile workspace is called while it has no saved name yet. */
const NEW_PROFILE_THING = "the new profile";

function ProfileActions({
  creating,
  selected,
  disabled,
  notice,
  onSave,
  onRemove,
}: {
  creating: boolean;
  selected: ProcessingRuleSet | undefined;
  disabled: boolean;
  notice: string | null;
  onSave: () => void;
  onRemove: () => void;
}) {
  const inUse = selected?.used_by_library_count ?? 0;
  return (
    <>
      {notice ? (
        <p role="status" className="text-sm font-medium text-mm-text1">
          {notice}
        </p>
      ) : null}
      <div className="mm-profile-actions">
        <button
          type="button"
          className={mmActionButtonClass({ variant: "primary" })}
          disabled={disabled}
          onClick={onSave}
        >
          {creating ? "Create profile" : "Save profile"}
        </button>
        {!creating && selected ? (
          <button
            type="button"
            className={mmActionButtonClass({ variant: "danger-outline" })}
            disabled={disabled || inUse > 0}
            onClick={onRemove}
            title={
              inUse > 0
                ? "Detach this profile from every workflow before removing it."
                : "Remove this unused profile."
            }
          >
            Remove profile
          </button>
        ) : null}
        {inUse > 0 ? (
          <span className="text-xs text-mm-text3">
            Used by {plural(inUse, "workflow", "workflows")}; removal is locked.
          </span>
        ) : null}
      </div>
    </>
  );
}

/** Setup › Rules › Profiles: the reusable profiles that decide which tracks a file keeps. */
export function ProfilesTab() {
  const me = useMeQuery();
  const ruleSets = useProcessingRuleSetsQuery();
  const libraries = useProcessingLibrariesQuery();
  const createRuleSet = useCreateProcessingRuleSet();
  const updateRuleSet = useUpdateProcessingRuleSet();
  const deleteRuleSet = useDeleteProcessingRuleSet();
  const editable = canEdit(me.data?.role);

  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [creating, setCreating] = useState(false);
  const [draft, setDraft] = useState<ProcessingRuleSetWrite | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [confirmingRemove, setConfirmingRemove] = useState(false);
  // Counts explicit switches of profile, so the ordering editor closes on a switch and stays open on a save.
  const [switches, setSwitches] = useState(0);

  // Until something is chosen, the first profile is open; a removed one gives way to the next.
  useEffect(() => {
    if (creating || !ruleSets.data) return;
    const selected =
      ruleSets.data.find((row) => row.id === selectedId) ?? ruleSets.data[0];
    if (!selected) {
      setSelectedId(null);
      setDraft(null);
      return;
    }
    if (selected.id !== selectedId || draft === null) {
      setSelectedId(selected.id);
      setDraft(writeFromProcessingRuleSet(selected));
    }
  }, [creating, draft, ruleSets.data, selectedId]);

  const rows = ruleSets.data ?? [];
  const selectedRuleSet = rows.find((row) => row.id === selectedId);

  // A saved profile is dirty once its draft diverges from what is on the server; a new one is dirty
  // from the moment its workspace opens, since nothing about it exists to fall back to.
  const dirtyThing = creating
    ? NEW_PROFILE_THING
    : selectedRuleSet &&
        draft &&
        !sameRuleSet(draft, writeFromProcessingRuleSet(selectedRuleSet))
      ? selectedRuleSet.name
      : null;
  useUnsavedChanges(dirtyThing);
  const { confirmLeave, dialog: leaveDialog } = useLeaveConfirmation();

  if (ruleSets.isPending || me.isPending) {
    return <PageLoading label="Loading profiles" />;
  }
  if (ruleSets.isError) {
    return <SettingsLoadError what="profiles" />;
  }

  const disabled =
    !editable || createRuleSet.isPending || updateRuleSet.isPending;
  const binding: RuleSetBinding | null = draft
    ? {
        draft,
        disabled,
        change: (key, value) =>
          setDraft((current) =>
            current
              ? { ...current, [key]: value }
              : /* istanbul ignore next -- @preserve */ current,
          ),
      }
    : null;
  const usedBy = (libraries.data ?? [])
    .filter(
      (library) => selectedId !== null && library.rule_set_id === selectedId,
    )
    .map((library) => library.name);

  const open = (id: number | null, next: ProcessingRuleSetWrite | null) => {
    setSelectedId(id);
    setDraft(next);
    setNotice(null);
  };

  const startNewProfile = () => {
    setCreating(true);
    setSwitches((n) => n + 1);
    open(null, { ...EMPTY_RULE_SET });
  };

  const switchTo = (id: number) => {
    const selected = rows.find((row) => row.id === id);
    setCreating(false);
    setSwitches((n) => n + 1);
    open(
      id,
      selected
        ? writeFromProcessingRuleSet(selected)
        : /* istanbul ignore next -- @preserve */ null,
    );
  };

  const save = () => {
    if (!draft || !draft.name.trim()) {
      setNotice("Give this profile a name before saving it.");
      return;
    }
    setNotice(null);
    const named = { ...draft, name: draft.name.trim() };
    (creating || selectedId === null
      ? createRuleSet.mutateAsync(named)
      : updateRuleSet.mutateAsync({ id: selectedId, data: named })
    )
      .then((saved) => {
        setCreating(false);
        open(saved.id, writeFromProcessingRuleSet(saved));
        setNotice(`${saved.name} was saved.`);
      })
      .catch((error: unknown) =>
        setNotice(errorMessage(error, "That profile could not be saved.")),
      );
  };

  const remove = () => {
    setConfirmingRemove(false);
    if (!selectedRuleSet || selectedRuleSet.used_by_library_count > 0) return;
    deleteRuleSet.mutate(selectedRuleSet.id, {
      onSuccess: () => {
        open(null, null);
        setNotice(`${selectedRuleSet.name} was removed.`);
      },
      onError: (error) =>
        setNotice(errorMessage(error, "That profile could not be removed.")),
    });
  };

  return (
    <div className="mm-quiet-stack" data-testid="processing-rule-set-workspace">
      <QuietSection
        headingId="processing-rule-set-profiles-heading"
        heading="Track rules"
        aside={<SaveModelNote model="explicit" />}
      >
        {!creating ? (
          <PageToolbarAddButton
            label="New profile"
            disabled={!editable}
            onClick={() => confirmLeave(dirtyThing, startNewProfile)}
          />
        ) : null}
        <ProfileBar
          ruleSets={rows}
          selectedId={selectedId}
          creating={creating}
          binding={binding}
          usedBy={usedBy}
          onSelect={(id) => confirmLeave(dirtyThing, () => switchTo(id))}
        />
        {!binding ? (
          <div className="mt-5">
            <p className="text-sm font-medium text-mm-text1">No profiles yet</p>
            <p className="mm-quiet-note mt-1">
              Create one, then assign it under Workflows.
            </p>
          </div>
        ) : (
          <div className="mm-profile-body space-y-5">
            <ProfileRules
              binding={binding}
              editable={editable}
              resetKey={String(switches)}
            />
            <ProfileActions
              creating={creating}
              selected={selectedRuleSet}
              disabled={disabled}
              notice={notice}
              onSave={save}
              onRemove={() => setConfirmingRemove(true)}
            />
          </div>
        )}
      </QuietSection>

      {confirmingRemove && selectedRuleSet ? (
        <ConfirmDialog
          title={`Remove the profile “${selectedRuleSet.name}”?`}
          confirmLabel="Remove profile"
          tone="danger"
          testId="remove-rule-set-dialog"
          onCancel={() => setConfirmingRemove(false)}
          onConfirm={remove}
        />
      ) : null}
      {leaveDialog}
    </div>
  );
}
