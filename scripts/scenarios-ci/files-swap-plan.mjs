// What a server ended at each point of a library clean leaves on disk, and what is left once Weir has started again: the same plan as the
// contract suite's SwapCrashPlan (apps/server/tests/Weir.Contract.Tests/Jobs/SwapCrashPlan.cs), here for the installed server on NTFS.
// Paths are relative to the library folder, or, for a kept original that goes to an originals folder of its own, to that folder.
const FILM = "Film (2020)/Film (2020).mkv";
const BACKUP = "Film (2020)/Film (2020).weir-bak.mkv";
const COPY = "Film (2020)/Film (2020).weir-tmp.mkv";
const KEPT_BESIDE = `.weir-originals/${FILM}`;

/** Every point of a swap, in the order it passes them (SwapPoint in Weir.Infrastructure/LibraryMode/SwapProgress.cs). */
export const POINTS = ["Started", "CopyChecked", "CommitRecorded", "OriginalMovedAside", "CleanedCopyInPlace", "CommittedRecorded", "BackupDealtWith", "FinishedRecorded"];

/** Whether the cleaned copy has taken the original's name by this point, so the cleaned film is what must be left. */
export const commits = (point) => POINTS.indexOf(point) >= POINTS.indexOf("CleanedCopyInPlace");

const sorted = (files) => [...files].sort();

/**
 * The files under the library folder, and under the originals folder when the original is kept somewhere else, at the moment the server
 * was ended. The place for the kept original is reserved (an empty file) from the point the commit is recorded.
 * @param {{keepOriginal: boolean, keptElsewhere?: boolean}} options
 */
export function filesWhenStopped(point, { keepOriginal, keptElsewhere = false }) {
  if (!POINTS.includes(point)) throw new Error(`${point} is not a point of a swap.`);
  const reserved = keepOriginal && POINTS.indexOf(point) >= POINTS.indexOf("CommitRecorded");
  const inLibrary = {
    Started: [FILM],
    CopyChecked: [FILM, COPY],
    CommitRecorded: [FILM, COPY],
    OriginalMovedAside: [BACKUP, COPY],
    CleanedCopyInPlace: [FILM, BACKUP],
    CommittedRecorded: [FILM, BACKUP],
    BackupDealtWith: [FILM],
    FinishedRecorded: [FILM],
  }[point];
  const reservation = reserved ? [keptElsewhere ? [] : [KEPT_BESIDE], keptElsewhere ? [FILM] : []] : [[], []];
  return { library: sorted([...inLibrary, ...reservation[0]]), elsewhere: sorted(reservation[1]) };
}

/** The files once the server has started again and put the swap right: the film, and its kept original when it was cleaned. */
export function filesAfterRecovery(point, { keepOriginal, keptElsewhere = false }) {
  const kept = keepOriginal && commits(point);
  return {
    library: sorted(kept && !keptElsewhere ? [FILM, KEPT_BESIDE] : [FILM]),
    elsewhere: sorted(kept && keptElsewhere ? [FILM] : []),
  };
}

export const KEPT_BESIDE_PATH = KEPT_BESIDE;
export const FILM_PATH = FILM;
