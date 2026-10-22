import { useState } from "react";

import { PageLoading } from "../../../../components/shared/page-loading";
import { Panel } from "../../../../components/panels/panel";
import { quietActionRowClass } from "../../../../components/shared/quiet-section";
import { canEdit } from "../../../../lib/auth/can-edit";
import { useMeQuery } from "../../../../lib/auth/queries";
import type {
  DirectPlayDevice,
  DirectPlayDevices,
} from "../../../../lib/processing/direct-play-api";
import {
  useDirectPlayDevicesQuery,
  useDirectPlayDevicesSaveMutation,
} from "../../../../lib/processing/direct-play-queries";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { errorMessage } from "../../../../lib/api/error-message";
import { SaveModelNote } from "../../save-model-note";
import { SettingsLoadError } from "../../settings-load-error";
import { useUnsavedChanges } from "../../unsaved-changes";

/** What the server names the sources that ship with Weir, as opposed to a link to a vendor's page. */
const BUILT_IN_SOURCE = "builtin";

/** Splits "https://… (checked 2026-08-13)" into its link and its date. */
function parseSource(source: string): {
  url: string | null;
  checked: string | null;
} {
  const url = source.match(/https?:\/\/\S+/)?.[0] ?? null;
  const checked = source.match(/checked\s+(\d{4}-\d{2}-\d{2})/i)?.[1] ?? null;
  return { url, checked };
}

function DeviceSource({
  device,
}: {
  device: DirectPlayDevice;
}): React.ReactElement {
  const { url, checked } = parseSource(device.source);
  if (!url) {
    return (
      <span>
        {device.source === BUILT_IN_SOURCE ? "Built in" : device.source}
      </span>
    );
  }
  return (
    <span>
      <a
        className="underline-offset-2 hover:underline"
        href={url}
        target="_blank"
        rel="noopener noreferrer"
        aria-label={`Source for ${device.name}`}
      >
        Source
      </a>
      {checked ? ` · checked ${checked}` : null}
    </span>
  );
}

/**
 * Which devices the operator owns, so each file can say whether it will play on them without
 * the media server converting it (#467). Information only: it changes the badge on files and
 * nothing about how a file is processed. It is also the list of devices a later "convert video so
 * my TV can play it directly" rule will aim at.
 */
export function PlaybackDevicesSection() {
  const me = useMeQuery();
  const q = useDirectPlayDevicesQuery();

  if (q.isPending || me.isPending) {
    return <PageLoading label="Loading your playback devices" />;
  }
  if (q.isError) {
    return <SettingsLoadError what="playback devices" />;
  }

  // Keyed by the saved selection, so ticks start from it and a change saved elsewhere replaces them.
  const savedKey = q.data.devices
    .filter((device) => device.selected)
    .map((device) => device.id)
    .join(",");
  return (
    <DeviceChecklist
      key={savedKey}
      data={q.data}
      editable={canEdit(me.data?.role)}
    />
  );
}

function DeviceChecklist({
  data,
  editable,
}: {
  data: DirectPlayDevices;
  editable: boolean;
}) {
  const save = useDirectPlayDevicesSaveMutation();
  const devices = data.devices;
  const [selected, setSelected] = useState<Set<string>>(
    () => new Set(devices.filter((device) => device.selected).map((d) => d.id)),
  );
  const dirty = devices.some((d) => d.selected !== selected.has(d.id));
  const canSave = editable && dirty && !save.isPending;
  useUnsavedChanges(dirty ? "your playback devices" : null);

  const toggle = (id: string) => {
    if (!editable) return;
    setSelected((previous) => {
      const next = new Set(previous);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  return (
    <Panel
      title="Devices you watch on"
      headingId="processing-direct-play-heading"
      padded
      count="Each file shows whether these can Direct Play it: your media server plays it as it is, without converting. Information only."
      aside={<SaveModelNote model="explicit" />}
      dataTestId="processing-direct-play-section"
    >
      {data.customised ? (
        <p
          className="mm-quiet-note mb-3"
          data-testid="processing-direct-play-customised"
        >
          This list comes from your own direct-play-devices.json in the Weir
          data folder.
        </p>
      ) : null}
      {!editable ? (
        <p className="mm-quiet-note mb-3">
          Only an operator or admin can change which devices are chosen.
        </p>
      ) : null}
      {devices.length === 0 ? (
        <p className="text-sm text-mm-text3">No devices are listed.</p>
      ) : (
        <ul className="mm-device-list">
          {devices.map((device) => (
            <li key={device.id} className="mm-device">
              <input
                id={`direct-play-device-${device.id}`}
                type="checkbox"
                className="mm-device__check"
                checked={selected.has(device.id)}
                disabled={!editable || save.isPending}
                onChange={() => toggle(device.id)}
              />
              <div>
                <label
                  htmlFor={`direct-play-device-${device.id}`}
                  className="mm-device__name"
                >
                  {device.name}
                </label>
                <p className="mm-device__note">{device.note}</p>
              </div>
              <p className="mm-device__source">
                <DeviceSource device={device} />
              </p>
            </li>
          ))}
        </ul>
      )}
      {dirty || save.isError ? (
        <div className={`${quietActionRowClass} mt-4`}>
          <button
            type="button"
            className={mmActionButtonClass({ variant: "primary" })}
            disabled={!canSave}
            onClick={() =>
              save.mutate(
                devices.filter((d) => selected.has(d.id)).map((d) => d.id),
              )
            }
          >
            {save.isPending ? "Saving…" : "Save devices"}
          </button>
          {save.isError ? (
            <span
              className="mm-status-text text-sm"
              data-status="broken"
              role="alert"
            >
              {errorMessage(save.error, "Save failed.")}
            </span>
          ) : null}
        </div>
      ) : null}
    </Panel>
  );
}
