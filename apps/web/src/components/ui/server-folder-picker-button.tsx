import { useEffect, useId, useState } from "react";

import { errorMessage } from "../../lib/api/error-message";
import {
  fetchServerDirectories,
  type DirectoryBrowseResponse,
} from "../../lib/system/directory-browser-api";
import { mmActionButtonClass } from "../../lib/ui/mm-control-roles";
import { examplePath } from "../../lib/ui/platform";
import { useModalFocus } from "../../lib/ui/use-modal-focus";

type Browse = {
  data: DirectoryBrowseResponse | null;
  loading: boolean;
  error: string | null;
  notice: string | null;
};

/**
 * Lists the folders at `path` on the machine running Weir. A path that cannot be opened falls back
 * to the drive list with a notice, so a stale or mistyped value never leaves the picker empty.
 */
function useBrowse(path: string | null): Browse {
  const [state, setState] = useState<Browse>({
    data: null,
    loading: true,
    error: null,
    notice: null,
  });
  useEffect(() => {
    let cancelled = false;
    const settle = (next: Omit<Browse, "loading">) => {
      if (!cancelled) setState({ ...next, loading: false });
    };
    setState((current) => ({ ...current, loading: true, error: null }));
    fetchServerDirectories(path)
      .then((data) => settle({ data, error: null, notice: null }))
      .catch(async (err: unknown) => {
        const failure = errorMessage(err, "Folder browser unavailable.");
        if (!path) {
          settle({ data: null, error: failure, notice: null });
          return;
        }
        try {
          const roots = await fetchServerDirectories(null);
          settle({
            data: roots,
            error: null,
            notice: `"${path}" could not be opened. Showing available drives instead.`,
          });
        } catch {
          // The drive list failed too: the first failure is the one worth showing.
          settle({ data: null, error: failure, notice: null });
        }
      });
    return () => {
      cancelled = true;
    };
  }, [path]);
  return state;
}

function FolderList({
  browse,
  onOpen,
  onChoose,
}: {
  browse: Browse;
  onOpen: (path: string) => void;
  onChoose: (path: string) => void;
}) {
  if (browse.loading) {
    return <div className="mm-folder-picker__empty">Loading folders...</div>;
  }
  if (browse.error) {
    return (
      <div className="mm-status-text text-sm" data-status="broken" role="alert">
        {browse.error}
      </div>
    );
  }
  const entries = browse.data?.entries ?? [];
  if (entries.length === 0) {
    return (
      <div className="mm-folder-picker__empty">No folders found here.</div>
    );
  }
  return (
    <div className="space-y-2">
      {entries.map((entry) => (
        <div key={entry.path} className="mm-folder-picker__entry">
          <button
            type="button"
            className="mm-folder-picker__entry-open"
            onClick={() => onOpen(entry.path)}
          >
            <span className="mm-folder-picker__entry-name">{entry.name}</span>
            <span className="mm-folder-picker__entry-path">
              {entry.description ?? entry.path}
            </span>
          </button>
          <button
            type="button"
            className={mmActionButtonClass({ variant: "tertiary" })}
            onClick={() => onOpen(entry.path)}
          >
            Open
          </button>
          <button
            type="button"
            className={mmActionButtonClass({ variant: "secondary" })}
            onClick={() => onChoose(entry.path)}
          >
            Select
          </button>
        </div>
      ))}
    </div>
  );
}

function PathBar({
  browse,
  onGo,
  onUp,
  onDrives,
  onChoose,
}: {
  browse: Browse;
  onGo: (path: string) => void;
  onUp: () => void;
  onDrives: () => void;
  onChoose: (path: string) => void;
}) {
  const [manualPath, setManualPath] = useState("");
  const current = browse.data?.current_path ?? null;
  return (
    <div className="mm-folder-picker__section">
      <form
        className="mm-folder-picker__go"
        onSubmit={(event) => {
          event.preventDefault();
          const next = manualPath.trim();
          if (next) onGo(next);
        }}
      >
        <input
          className="mm-input"
          value={manualPath}
          onChange={(event) => setManualPath(event.target.value)}
          placeholder={examplePath(
            String.raw`\\<host>\media or X:\Media`,
            "/media/tv",
          )}
          aria-label="Folder path"
        />
        <button
          type="submit"
          className={mmActionButtonClass({ variant: "secondary" })}
          disabled={browse.loading}
        >
          Go to path
        </button>
      </form>
      <div className="mm-folder-picker__nav">
        <button
          type="button"
          className={mmActionButtonClass({ variant: "secondary" })}
          disabled={browse.loading || !browse.data?.parent_path}
          onClick={onUp}
        >
          Up
        </button>
        <button
          type="button"
          className={mmActionButtonClass({ variant: "tertiary" })}
          disabled={browse.loading || browse.data?.current_path === null}
          onClick={onDrives}
        >
          Drives
        </button>
        {current ? (
          <button
            type="button"
            className={mmActionButtonClass({ variant: "primary" })}
            onClick={() => onChoose(current)}
          >
            Use this folder
          </button>
        ) : null}
        <div className="mm-folder-picker__current">
          <span>{current ?? "Available drives"}</span>
        </div>
      </div>
      <p className="mm-folder-picker__help">
        Windows supports local drives, mapped drives, and UNC shares such as{" "}
        <span className="font-mono">{String.raw`\\<host>\media`}</span>. Docker
        installs must use container-visible paths such as{" "}
        <span className="font-mono">/media/tv</span>.
      </p>
      {browse.notice ? (
        <p className="mm-status-text mt-3 text-sm" data-status="attention">
          {browse.notice}
        </p>
      ) : null}
    </div>
  );
}

function FolderPickerDialog({
  title,
  startPath,
  onChoose,
  onClose,
}: {
  title: string;
  startPath: string | null;
  onChoose: (path: string) => void;
  onClose: () => void;
}) {
  const titleId = useId();
  const [path, setPath] = useState(startPath);
  const browse = useBrowse(path);
  const panelRef = useModalFocus<HTMLDivElement>({ onClose });
  return (
    <div
      className="mm-folder-picker"
      role="dialog"
      aria-modal="true"
      aria-labelledby={titleId}
    >
      <div ref={panelRef} className="mm-folder-picker__panel" tabIndex={-1}>
        <div className="mm-folder-picker__head">
          <div>
            <p className="mm-folder-picker__eyebrow">Filesystem</p>
            <h2 id={titleId} className="mm-folder-picker__title">
              {title}
            </h2>
            <p className="mm-folder-picker__lead">
              Pick a folder visible to the machine running Weir, or jump to a
              UNC/Docker path directly.
            </p>
          </div>
          <button
            type="button"
            className={mmActionButtonClass({ variant: "secondary" })}
            onClick={onClose}
          >
            Close
          </button>
        </div>
        <PathBar
          browse={browse}
          onGo={setPath}
          onUp={() => setPath(browse.data?.parent_path ?? null)}
          onDrives={() => setPath(null)}
          onChoose={onChoose}
        />
        <div className="mm-folder-picker__list">
          <FolderList browse={browse} onOpen={setPath} onChoose={onChoose} />
        </div>
      </div>
    </div>
  );
}

/** A Browse button that opens a picker over the folders on the machine running Weir. */
export function ServerFolderPickerButton({
  title,
  value,
  disabled,
  onSelect,
}: {
  title: string;
  value: string;
  disabled?: boolean;
  onSelect: (path: string) => void;
}) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <button
        type="button"
        className={mmActionButtonClass({ variant: "tertiary" })}
        title={title}
        disabled={disabled}
        onClick={() => setOpen(true)}
      >
        Browse
      </button>
      {open ? (
        <FolderPickerDialog
          title={title}
          startPath={value.trim() || null}
          onChoose={(path) => {
            onSelect(path);
            setOpen(false);
          }}
          onClose={() => setOpen(false)}
        />
      ) : null}
    </>
  );
}
