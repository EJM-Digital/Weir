/**
 * Library: the files already on your storage, and what Weir would do to each (library mode, #505/#568).
 * The header holds the workflow picker, the search, and one row of chips that carries the numbers and the filters
 * together, because a count nobody can act on is decoration; the table groups files by title and season, a page at a time. Every number
 * comes from the library scan on the server; nothing is counted in the browser.
 */
import { useCallback, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { useSearchParams } from "react-router-dom";

import { Panel } from "../../components/panels/panel";
import { ApiEntryError } from "../../components/shared/api-entry-error";
import { PageLoading } from "../../components/shared/page-loading";
import { PageToolbarButtons } from "../../components/shell/page-toolbar";
import { ShellHeaderSlot } from "../../components/shell/shell-header-context";
import { useTitleLineFit } from "../../components/shell/title-line-fit";
import { useActivityStreamInvalidations } from "../../lib/activity/use-activity-stream-invalidation";
import type { LibraryFileFilters } from "../../lib/processing/library-mode-api";
import {
  useLibraryFilesQuery,
  useLibraryOverviewQuery,
  useLibrarySettingsQuery,
  useTriggerLibraryScan,
} from "../../lib/processing/library-mode-queries";
import { useProcessingLibrariesQuery } from "../../lib/processing/libraries-queries";
import { processingKeys } from "../../lib/processing/query-keys";
import { useMediaQuery } from "../../lib/ui/use-media-query";
import { useDebouncedValue } from "../../lib/ui/use-debounced-value";
import { useNow } from "../../lib/ui/use-now";
import { ColumnsMenu } from "../../components/shared/columns-menu";
import type { TableSort } from "../../lib/ui/table-columns";
import { useTableColumns } from "../../lib/ui/use-table-columns";
import { LibraryCleanActions } from "./library-clean-actions";
import { LibraryCleanConfirm } from "./library-clean-dialog";
import { LibraryFileDrawer } from "./library-file-drawer";
import { readLastLibrary, saveLastLibrary } from "./last-library";
import {
  LIBRARY_CHIP_COUNT,
  LibraryFilters,
  LibraryReasonSelect,
  NO_FILTER,
  statusFromShow,
  type LibraryFilter,
} from "./library-filters";
import {
  emptyStatusLine,
  filesCount,
  groupFiles,
  headerLead,
  savingLine,
} from "./library-model";
import {
  LIBRARY_COLUMNS,
  LIBRARY_SORT_KEYS,
  type LibraryColumnId,
} from "./library-columns";
import { LibraryPager } from "./library-pager";
import { LibraryPicker } from "./library-picker";
import { LibraryCheckAgain, LibraryScanStatus } from "./library-scan-status";
import { LibrarySetupButton } from "./library-setup-button";
import { LibrarySetupPanel } from "./library-setup-panel";
import { LibrarySetupPrompt } from "./library-setup-prompt";
import { LibraryTable } from "./library-table";
import { useLibraryClean, type CleanRequest } from "./use-library-clean";

const PAGE_SIZE = 200;
/** What a link can open the page onto, read once: the address then holds only the workflow and the status chosen. */
const OPENED_ONCE = ["q", "path"] as const;

/** The address as it goes on, without what a link opened the page onto and the page no longer shows. */
function addressAfterOpening(params: URLSearchParams): URLSearchParams {
  const next = new URLSearchParams(params);
  for (const name of OPENED_ONCE) next.delete(name);
  return next;
}
/** Typing narrows the table, but not on every keystroke: the server reads the whole index for each search. */
const SEARCH_DEBOUNCE_MS = 300;
/** A clean finishes on the server minutes after the button, and a library is a long list to re-read. */
const REFRESH_THROTTLE_MS = 5_000;
/** "Checked 5 min ago" counts in minutes, so it only needs to move once a minute. */
const CLOCK_TICK_MS = 60_000;

/**
 * What gives way, in order, when the title line is short of room: the search shortens and Check again becomes a mark,
 * the search becomes a mark, Library setup becomes a mark, then the workflow picker moves into the Files card's header.
 * The chips fold into "More" only after all of that.
 */
const SEARCH_SHORTENED = 1;
const SETUP_AS_MARK = 3;
const PICKER_IN_CARD = 4;

function fileFilters(
  filter: LibraryFilter,
  query: string,
  page: number,
  sort: TableSort<LibraryColumnId> | null,
): LibraryFileFilters {
  return {
    ...(filter.status ? { status: filter.status } : {}),
    ...(filter.problem ? { problem: filter.problem } : {}),
    ...(query ? { q: query } : {}),
    ...(sort && sort.id !== "select"
      ? { sort: LIBRARY_SORT_KEYS[sort.id], direction: sort.direction }
      : /* istanbul ignore next -- @preserve */ {}),
    page,
    page_size: PAGE_SIZE,
  };
}

function NoLibrary() {
  return (
    <div className="mm-page mm-library" data-testid="library-page">
      <p className="mm-library-empty">
        No workflow is set up yet. Add one in Setup › Workflows, then come back
        here and tell Weir which folders the files you already have sit in.
      </p>
    </div>
  );
}

export function LibraryPage(): React.ReactElement {
  const libraries = useProcessingLibrariesQuery();
  const [params, setParams] = useSearchParams();
  const [libraryId, setLibraryId] = useState<number | null>(null);
  const [lastLibrary] = useState(readLastLibrary);
  const [search, setSearch] = useState(params.get("q") ?? "");
  // The slice of the library lives in the address (no parameter is All), so a link can open straight onto it.
  const [filter, setFilter] = useState<LibraryFilter>(() => ({
    ...NO_FILTER,
    status: statusFromShow(params.get("show")),
  }));
  const [page, setPage] = useState(1);
  // A selection belongs to the page it was made on: anything that changes the rows on screen clears it.
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [openPath, setOpenPath] = useState<string | null>(params.get("path"));
  const [setupOpen, setSetupOpen] = useState(false);
  // A new sort starts at the first page, and what was selected belongs to rows that are no longer on screen.
  const columns = useTableColumns(LIBRARY_COLUMNS, {
    onSortChange: () => {
      setPage(1);
      setSelected(new Set());
    },
  });
  const now = useNow(CLOCK_TICK_MS);
  const query = useDebouncedValue(search.trim(), SEARCH_DEBOUNCE_MS);
  const tableTop = useRef<HTMLDivElement>(null);

  // The chosen library survives a reload and a link, so "open this library" is a plain URL. Without one in the
  // address, the library last picked in this browser opens.
  const chosen = useMemo(() => {
    const list = libraries.data ?? [];
    const wanted = libraryId ?? (Number(params.get("library")) || lastLibrary);
    return list.find((l) => l.id === wanted) ?? list[0] ?? null;
  }, [libraries.data, libraryId, params, lastLibrary]);
  const chosenId = chosen?.id ?? 0;

  const libraryKeys = useMemo(
    () => [
      processingKeys.libraryFiles(chosenId),
      processingKeys.libraryOverview(chosenId),
    ],
    [chosenId],
  );
  useActivityStreamInvalidations(libraryKeys, {
    throttleMs: REFRESH_THROTTLE_MS,
  });

  const sort = columns.sort;
  const filters = useMemo(
    () => fileFilters(filter, query, page, sort),
    [filter, query, page, sort],
  );
  const setupSettings = useLibrarySettingsQuery(chosenId, Boolean(chosen));
  const overview = useLibraryOverviewQuery(chosenId, Boolean(chosen));
  const rescan = useTriggerLibraryScan(chosenId);
  // The header gives up its controls' words, then its picker, then the last chips, until what is left shows whole.
  const [chipsRow, setChipsRow] = useState<HTMLDivElement | null>(null);
  const [pickerSlot, setPickerSlot] = useState<HTMLDivElement | null>(null);
  const sharesTitleLine = useMediaQuery("(min-width: 921px)");
  const steps = pickerSlot ? PICKER_IN_CARD : SETUP_AS_MARK;
  const { stage, folded } = useTitleLineFit({
    row: chipsRow,
    stages: sharesTitleLine ? steps : 0,
    chips: LIBRARY_CHIP_COUNT,
    refit: `${JSON.stringify(overview.data?.totals.by_status)}|${filter.status}|${search !== ""}|${pickerSlot !== null}`,
  });
  const files = useLibraryFilesQuery(chosenId, filters, Boolean(chosen));
  const flow = useLibraryClean(chosenId, (request) => {
    if (request.source === "selection") setSelected(new Set());
  });
  const groups = useMemo(
    () => groupFiles(files.data?.files ?? []),
    [files.data],
  );

  const showRows = useCallback((change: () => void) => {
    change();
    setSelected(new Set());
  }, []);

  const pick = useCallback(
    (id: number) => {
      showRows(() => {
        setLibraryId(id);
        setPage(1);
        setOpenPath(null);
        setSetupOpen(false);
      });
      saveLastLibrary(id);
      const next = addressAfterOpening(params);
      next.set("library", String(id));
      setParams(next, { replace: true });
    },
    [params, setParams, showRows],
  );

  const toggle = useCallback((path: string) => {
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(path)) next.delete(path);
      else next.add(path);
      return next;
    });
  }, []);

  if (libraries.isPending) {
    return <PageLoading label="Loading your workflows" />;
  }
  if (libraries.isError) {
    return (
      <div className="mm-page">
        <ApiEntryError error={libraries.error} />
      </div>
    );
  }
  if (!chosen) return <NoLibrary />;

  const totals = overview.data?.totals;
  const lead = headerLead(totals, Boolean(overview.data?.schedule.enabled));
  const loaded = files.data?.files ?? [];
  const openFile = loaded.find((f) => f.path === openPath) ?? null;
  const selectedSaving = loaded
    .filter((f) => selected.has(f.path))
    .reduce((sum, f) => sum + f.estimated_bytes_saved, 0);
  const forOpenFile = (request: CleanRequest | null | undefined) =>
    request?.source === "file" && request.paths[0] === openPath;
  const fileOutcome =
    flow.outcome && forOpenFile(flow.outcome.request)
      ? flow.outcome.result
      : null;

  const turnPage = (next: number) => {
    showRows(() => setPage(next));
    tableTop.current?.scrollIntoView?.({ block: "start" });
  };

  // A library with no folders has nothing to check yet: setting it up is the page.
  const savedSetup = setupSettings.data;
  const needsSetup = savedSetup?.library_folders.length === 0;
  const scan = overview.data?.scan ?? files.data?.scan ?? null;
  const chooseFilter = (next: LibraryFilter) =>
    showRows(() => {
      setFilter(next);
      setPage(1);
      const address = addressAfterOpening(params);
      if (next.status) address.set("show", next.status);
      else address.delete("show");
      setParams(address, { replace: true });
    });
  const pickerInCard = pickerSlot !== null && stage >= PICKER_IN_CARD;
  const picker = (
    <LibraryPicker
      libraries={libraries.data}
      chosenId={chosen.id}
      onPick={pick}
      countFor={(id) => (id === chosen.id ? (totals?.files ?? null) : null)}
    />
  );
  const filterControls = (
    <LibraryFilters
      search={search}
      onSearch={(value) =>
        showRows(() => {
          setSearch(value);
          setPage(1);
        })
      }
      overview={overview.data}
      filter={filter}
      onFilter={chooseFilter}
      fit={stage}
      folded={folded}
      onRow={setChipsRow}
    />
  );
  const header = (
    <>
      <ShellHeaderSlot>
        <div
          className="mm-library-controls"
          data-testid="library-controls"
          data-tight={stage >= SETUP_AS_MARK ? "" : undefined}
        >
          {pickerInCard ? null : picker}
          {needsSetup ? null : filterControls}
        </div>
        {pickerInCard ? createPortal(picker, pickerSlot) : null}
      </ShellHeaderSlot>
      {needsSetup ? null : (
        <PageToolbarButtons>
          <LibraryCheckAgain
            scan={scan}
            rescan={rescan}
            iconOnly={stage >= SEARCH_SHORTENED}
          />
          {savedSetup ? (
            <LibrarySetupButton
              iconOnly={stage >= SETUP_AS_MARK}
              onOpen={() => setSetupOpen(true)}
            />
          ) : null}
        </PageToolbarButtons>
      )}
    </>
  );
  const setupPanel =
    setupOpen && savedSetup ? (
      <LibrarySetupPanel
        key={chosen.id}
        workflow={chosen}
        settings={savedSetup}
        onClose={() => setSetupOpen(false)}
      />
    ) : null;

  if (needsSetup) {
    return (
      <div className="mm-page mm-library" data-testid="library-page">
        {header}
        <LibrarySetupPrompt
          libraryName={chosen.name}
          onSetUp={() => setSetupOpen(true)}
        />
        {setupPanel}
      </div>
    );
  }

  return (
    <div className="mm-page mm-library" data-testid="library-page">
      {header}

      <Panel
        className="mm-library-files"
        title="Files"
        description={lead}
        aside={
          <>
            <div ref={setPickerSlot} className="contents" />
            <LibraryReasonSelect
              overview={overview.data}
              filter={filter}
              onFilter={chooseFilter}
            />
            <LibraryScanStatus
              scan={scan}
              schedule={overview.data?.schedule}
              now={now}
              rescan={rescan}
            />
            <p className="mm-library-saving">{savingLine(totals)}</p>
            <ColumnsMenu table={columns} />
          </>
        }
        count={
          totals ? <span title={lead}>{filesCount(totals)}</span> : undefined
        }
      >
        <LibraryCleanActions
          selected={selected}
          selectedSaving={selectedSaving}
          known={loaded}
          onClearSelection={() => setSelected(new Set())}
          flow={flow}
        />

        <div ref={tableTop} className="mm-library-results">
          {files.isPending ? (
            <PageLoading label="Reading this workflow" />
          ) : files.isError ? (
            <ApiEntryError error={files.error} />
          ) : groups.length === 0 ? (
            <p className="mm-library-empty" data-testid="library-empty">
              {totals && totals.files > 0
                ? filter.status && !query
                  ? emptyStatusLine(filter.status)
                  : "Nothing here matches what you asked for. Clear the filters to see the whole library."
                : "This workflow has not been scanned yet, or its folders hold nothing Weir reads. Check again, or change its folders in Library setup."}
            </p>
          ) : (
            <LibraryTable
              libraryName={chosen.name}
              groups={groups}
              columns={columns}
              openPath={openPath}
              selected={selected}
              onToggle={toggle}
              onOpen={setOpenPath}
            />
          )}
        </div>

        {files.data ? (
          <LibraryPager
            page={page}
            pageSize={PAGE_SIZE}
            shown={loaded.length}
            total={files.data.total}
            loading={files.isPlaceholderData}
            hasSelection={selected.size > 0}
            onPage={turnPage}
          />
        ) : null}
      </Panel>

      {/* Keyed by the file, so opening another one starts its track choice again. */}
      {openFile ? (
        <LibraryFileDrawer
          key={openFile.path}
          libraryId={chosen.id}
          libraryName={chosen.name}
          file={openFile}
          onClose={() => setOpenPath(null)}
          onClean={(path, manual) =>
            flow.start({ source: "file", paths: [path], manual })
          }
          clean={{
            pending: forOpenFile(flow.latest) && flow.isPending("file"),
            failure: forOpenFile(flow.latest) ? flow.failure("file") : null,
            result: fileOutcome,
            onDismissResult: flow.dismissOutcome,
          }}
        />
      ) : null}

      <LibraryCleanConfirm flow={flow} />
      {setupPanel}
    </div>
  );
}
