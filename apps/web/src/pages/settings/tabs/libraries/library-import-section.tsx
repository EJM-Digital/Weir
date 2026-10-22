import { useState } from "react";

import { Field } from "../../../../components/shared/field";
import { QuietSection } from "../../../../components/shared/quiet-section";
import { errorMessage } from "../../../../lib/api/error-message";
import type { MediaManagerConnection } from "../../../../lib/media-managers/media-managers-api";
import type {
  DiscoverableProcessingLibrary,
  ProcessingLibraryDrift,
} from "../../../../lib/processing/library-managers-api";
import {
  useDiscoverProcessingLibraries,
  useImportDiscoveredProcessingLibraries,
  useProcessingLibraryDrift,
} from "../../../../lib/processing/libraries-queries";
import { connectionTitle } from "../../../../lib/ui/connection-title";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { plural } from "../../../../lib/ui/mm-plural";

const NO_MEDIA_TYPE_PROBLEM =
  "The media manager did not say whether this is Movies or TV episodes, so Weir cannot import it.";

function DiscoveredLibrary({
  item,
  selected,
  onSelect,
}: {
  item: DiscoverableProcessingLibrary;
  selected: boolean;
  onSelect: (selected: boolean) => void;
}) {
  const unavailable =
    item.already_imported ||
    Boolean(item.local_path_problem) ||
    !item.media_type;
  const problem =
    item.local_path_problem ||
    item.output_path_problem ||
    (item.media_type ? null : NO_MEDIA_TYPE_PROBLEM);
  return (
    <label className="mm-discovered-library">
      <input
        type="checkbox"
        className="mt-1 h-4 w-4 shrink-0 accent-mm-accent"
        checked={selected}
        disabled={unavailable}
        onChange={(event) => onSelect(event.target.checked)}
      />
      <span className="min-w-0">
        <span className="block font-medium text-mm-text1">{item.name}</span>
        <span className="block break-all text-xs text-mm-text3">
          Manager path: {item.root_path || "Not supplied"}
        </span>
        {item.output_path ? (
          <span className="block break-all text-xs text-mm-text3">
            Processed output: {item.output_path}
          </span>
        ) : null}
        {item.already_imported ? (
          <span className="mm-status-text block text-xs" data-status="done">
            Already imported
          </span>
        ) : null}
        {problem ? (
          <span
            className="mm-status-text block text-xs"
            data-status="attention"
          >
            {problem}
          </span>
        ) : null}
      </span>
    </label>
  );
}

function DriftList({ items }: { items: ProcessingLibraryDrift[] }) {
  return (
    <div className="mt-6" aria-label="Workflow comparison">
      <h4 className="text-sm font-medium text-mm-text1">Comparison result</h4>
      {items.length === 0 ? (
        <p className="mm-status-text text-sm" data-status="done">
          No manager/workflow path differences were found.
        </p>
      ) : (
        items.map((item, index) => (
          <div
            key={`${item.kind}-${item.library_id ?? "new"}-${index}`}
            className="border-b border-mm-border py-2.5"
          >
            <p className="text-sm font-medium text-mm-text1">
              {item.library_name}
            </p>
            <p className="text-sm text-mm-text2">{item.detail}</p>
            {item.manager_value || item.weir_value ? (
              <p className="break-all text-xs text-mm-text3">
                Manager: {item.manager_value || "Not reported"} · Weir:{" "}
                {item.weir_value || "Not configured"}
              </p>
            ) : null}
          </div>
        ))
      )}
    </div>
  );
}

/**
 * Ask a connected manager which libraries it owns, import the ones chosen, or compare what Weir
 * has with what the manager reports. Comparing never changes an existing watched folder.
 */
export function LibraryImportSection({
  connections,
  onNotice,
}: {
  connections: MediaManagerConnection[];
  onNotice: (notice: string | null) => void;
}) {
  const discover = useDiscoverProcessingLibraries();
  const importDiscovered = useImportDiscoveredProcessingLibraries();
  const drift = useProcessingLibraryDrift();
  const [connectionId, setConnectionId] = useState("");
  const [selectedKeys, setSelectedKeys] = useState<string[]>([]);
  const chosen = Number.parseInt(connectionId, 10);

  const askManager = (
    run: (id: number) => Promise<unknown>,
    failure: string,
  ) => {
    onNotice(null);
    run(chosen).catch((error: unknown) =>
      onNotice(errorMessage(error, failure)),
    );
  };

  const discoverLibraries = () => {
    setSelectedKeys([]);
    askManager(
      (id) => discover.mutateAsync(id),
      "Weir could not discover workflows from that media manager.",
    );
  };

  const importSelected = () => {
    askManager(async (id) => {
      const created = await importDiscovered.mutateAsync({
        connectionId: id,
        keys: selectedKeys,
      });
      setSelectedKeys([]);
      await discover.mutateAsync(id);
      onNotice(
        `${plural(created.length, "workflow was", "workflows were")} imported. Review its local paths before enabling processing.`,
      );
    }, "Those workflows could not be imported.");
  };

  return (
    <QuietSection
      headingId="processing-libraries-import-heading"
      heading="Import from a media manager"
    >
      <p className="mm-quiet-note">
        Ask a connected manager which workflows it owns. Comparisons report path
        differences and never change an existing watched folder.
      </p>
      <div className="mt-5 flex flex-wrap items-end gap-2">
        <Field label="Media manager" width="medium">
          <select
            className="mm-input"
            value={connectionId}
            onChange={(event) => {
              setConnectionId(event.target.value);
              setSelectedKeys([]);
              discover.reset();
              drift.reset();
            }}
          >
            <option value="">Choose a manager</option>
            {connections.map((connection) => (
              <option key={connection.id} value={connection.id}>
                {connectionTitle(connection)}
              </option>
            ))}
          </select>
        </Field>
        <button
          type="button"
          className={mmActionButtonClass({ variant: "secondary" })}
          onClick={discoverLibraries}
          disabled={!connectionId || discover.isPending}
        >
          Discover workflows
        </button>
        <button
          type="button"
          className={mmActionButtonClass({ variant: "secondary" })}
          onClick={() =>
            askManager(
              (id) => drift.mutateAsync(id),
              "Weir could not compare workflows with that media manager.",
            )
          }
          disabled={!connectionId || drift.isPending}
        >
          Check for changes
        </button>
      </div>

      {discover.data ? (
        <div className="mt-5" aria-label="Discovered workflows">
          {discover.data.length === 0 ? (
            <p className="text-sm text-mm-text3">
              That manager did not report any workflows.
            </p>
          ) : (
            discover.data.map((item) => (
              <DiscoveredLibrary
                key={item.key}
                item={item}
                selected={selectedKeys.includes(item.key)}
                onSelect={(selected) =>
                  setSelectedKeys((current) =>
                    selected
                      ? [...current, item.key]
                      : current.filter((key) => key !== item.key),
                  )
                }
              />
            ))
          )}
          <button
            type="button"
            className={`${mmActionButtonClass({ variant: "primary" })} mt-4`}
            onClick={importSelected}
            disabled={selectedKeys.length === 0 || importDiscovered.isPending}
          >
            Import selected
          </button>
        </div>
      ) : null}

      {drift.data ? <DriftList items={drift.data} /> : null}
    </QuietSection>
  );
}
