import { EditorSubsection } from "./editor-subsection";
import type { DownloadClientSuggestion } from "../../../../lib/download-clients/download-clients-api";
import { useDownloadClientSuggestionsQuery } from "../../../../lib/download-clients/queries";
import type { ProcessingMediaType } from "../../../../lib/processing/libraries-api";

function Suggestion({
  item,
  watchedFolder,
  editable,
  onUseFolder,
}: {
  item: DownloadClientSuggestion;
  watchedFolder: string;
  editable: boolean;
  onUseFolder: (watched: string) => void;
}) {
  const suggested = item.suggested_watched_folder;
  if (!suggested || suggested === watchedFolder.trim()) return null;
  return (
    <p className="text-sm text-mm-text2">
      {item.label}&apos;s completed-downloads folder is{" "}
      <code className="break-all">{suggested}</code>.{" "}
      {editable ? (
        <button
          type="button"
          className="mm-quiet-link"
          onClick={() => onUseFolder(suggested)}
        >
          Use this as the watched folder
        </button>
      ) : null}
    </p>
  );
}

/**
 * A connected download client only suggests a watched folder: it is not linked to the workflow, so Weir only
 * workflows get the same offer. Whether the folder matches is the folder chain's to say. Nothing shows when no
 * client has a folder to offer.
 */
export function LibraryDownloadClientSuggestions({
  mediaType,
  watchedFolder,
  editable,
  onUseFolder,
}: {
  mediaType: ProcessingMediaType;
  watchedFolder: string;
  editable: boolean;
  onUseFolder: (watched: string) => void;
}) {
  const clients = useDownloadClientSuggestionsQuery(mediaType);
  const offers = (clients.data ?? []).filter(
    (item) =>
      item.suggested_watched_folder &&
      item.suggested_watched_folder !== watchedFolder.trim(),
  );
  if (offers.length === 0) return null;
  return (
    <EditorSubsection
      title="Folder from your download client"
      detail="Your download client says where it saves finished downloads."
    >
      <div
        className="space-y-2"
        data-testid="library-download-client-suggestions"
      >
        {offers.map((item) => (
          <Suggestion
            key={item.connection_id}
            item={item}
            watchedFolder={watchedFolder}
            editable={editable}
            onUseFolder={onUseFolder}
          />
        ))}
      </div>
    </EditorSubsection>
  );
}
