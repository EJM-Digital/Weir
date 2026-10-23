using System.Globalization;
using Microsoft.Data.Sqlite;
using Weir.Core.LibraryMode;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.LibraryMode;

/// <summary>
/// The #568 Library view's reads: totals, facet breakdowns, the paged/sorted/filtered Files listing and the
/// Problems grouping. Every one is SQL over <c>library_files</c> and <c>library_file_facets</c> with the indexes
/// migration <c>0007_library_file_facets.sql</c> adds — issue #568 point 7 is explicit that a library of thousands
/// of files must not be aggregated in the browser, and none of these open a row's <c>probe_json</c> either: the
/// facts were derived once, when the scan wrote the row (<see cref="LibraryFileFactsReader"/>).
/// </summary>
public sealed class LibraryViewStore
{
    /// <summary>
    /// Every read here is over the scan's picture with what Weir has done to each file beside it. The join is on the
    /// path because a scan rewrites <c>library_files</c> from scratch: the marks are keyed by what does not change.
    /// </summary>
    private const string FromFiles =
        "FROM library_files AS f LEFT JOIN library_file_marks AS m " +
        "ON m.library_id = f.library_id AND m.path = f.path ";

    /// <summary>
    /// The paths of the files a library clean is queued or running for. One clean job per file is outstanding at a time, and
    /// its payload names the file, so this is a small read however large the library.
    /// </summary>
    private const string CleaningPaths =
        "SELECT json_extract(j.payload_json, '$.path') FROM jobs AS j WHERE j.job_kind = @clean_kind " +
        "AND j.status IN ('pending', 'leased') AND j.dedupe_key LIKE @clean_prefix ESCAPE '\\'";

    /// <summary>
    /// Where a file stands now: exactly one <see cref="LibraryFileStatus"/>, so the counts add up to the files. Left alone
    /// beats Cleaning, which beats Can't clean yet (still shared with a download, or unreadable), which beats Needs
    /// cleaning, which beats Matches. It reads the scan's verdict, what a person asked, and the clean jobs outstanding.
    /// </summary>
    private const string StatusSql =
        "(CASE " +
        "WHEN COALESCE(m.leave_alone, 0) = 1 THEN 'left_alone' " +
        "WHEN f.path IN (" + CleaningPaths + ") THEN 'cleaning' " +
        "WHEN f.classification = 'cannot_process' OR (f.classification = 'would_change' AND (f.problem_kind IS NOT NULL " +
        "OR (@cleans_hardlinked = 0 AND COALESCE(f.link_count, 1) > 1))) THEN 'cant_clean_yet' " +
        "WHEN f.classification = 'would_change' THEN 'needs_cleaning' " +
        "ELSE 'matches' END)";

    /// <summary>What a file counts for when a list is sorted by status: how far down the list it goes.</summary>
    private const int MatchesRank = 0;
    private const int NeedsCleaningRank = 1;
    private const int CleaningRank = 2;
    private const int CantCleanYetRank = 3;
    private const int CantCleanYetUnreadableRank = 4;
    private const int LeftAloneRank = 5;

    /// <summary>
    /// The order of a list sorted by status, which follows what each status means: done (matches), to do (needs cleaning),
    /// doing (cleaning), needs a look (can't clean yet), broken (can't clean yet because Weir cannot read or open the file,
    /// so it goes after the others) and left alone. Ascending reads in that order.
    /// </summary>
    private static readonly string StatusRankSql =
        $"(CASE {StatusSql} " +
        $"WHEN '{LibraryFileStatus.Matches}' THEN {MatchesRank} " +
        $"WHEN '{LibraryFileStatus.NeedsCleaning}' THEN {NeedsCleaningRank} " +
        $"WHEN '{LibraryFileStatus.Cleaning}' THEN {CleaningRank} " +
        $"WHEN '{LibraryFileStatus.CantCleanYet}' THEN (CASE WHEN f.problem_kind IN ('{LibraryProblems.Name(LibraryProblemKind.Unreadable)}', '{LibraryProblems.Name(LibraryProblemKind.NoPermission)}') " +
        $"THEN {CantCleanYetUnreadableRank} ELSE {CantCleanYetRank} END) " +
        $"ELSE {LeftAloneRank} END)";

    /// <summary>How many paths a Problems group carries inline before the operator has to open Files to see the rest.</summary>
    public const int ProblemSampleSize = 5;

    public async Task<LibraryTotals> TotalsAsync(UnitOfWork uow, long libraryId, LibraryFileQuery? filter = null)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var (where, parameters) = BuildWhere(libraryId, filter);
        var row = await uow.QuerySingleAsync(
            "SELECT COUNT(*), COALESCE(SUM(f.size_bytes), 0), " +
            "COALESCE(SUM(CASE WHEN f.classification = 'matches' THEN 1 ELSE 0 END), 0), " +
            "COALESCE(SUM(CASE WHEN f.classification = 'would_change' THEN 1 ELSE 0 END), 0), " +
            "COALESCE(SUM(CASE WHEN f.classification = 'cannot_process' THEN 1 ELSE 0 END), 0), " +
            "COALESCE(SUM(f.estimated_bytes_saved), 0), COALESCE(SUM(f.removed_audio_tracks), 0), " +
            "COALESCE(SUM(f.removed_subtitle_tracks), 0), " +
            "COALESCE(SUM(CASE WHEN m.cleaned_at IS NOT NULL THEN 1 ELSE 0 END), 0), " +
            "COALESCE(SUM(CASE WHEN COALESCE(m.leave_alone, 0) = 1 THEN 1 ELSE 0 END), 0), " +
            string.Join(
                ", ",
                LibraryFileStatus.All.Select(status => $"COALESCE(SUM(CASE WHEN {StatusSql} = '{status}' THEN 1 ELSE 0 END), 0)")) +
            " " + FromFiles + where,
            reader => new LibraryTotals(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
                reader.GetInt64(8), reader.GetInt64(9))
            {
                // LibraryFileStatus.All is in this order: needs cleaning, cleaning, matches, can't clean yet, left alone.
                ByStatus = new LibraryStatusCounts(
                    reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14)),
            },
            parameters).ConfigureAwait(false);
        // An aggregate with no GROUP BY always answers with one row, even over no files.
        return row!;
    }

    /// <summary>
    /// One facet's rows, most files first then by value, so the bars read top-down. Counts are per file, not per
    /// track: a file with three English audio tracks counts once under <c>eng</c>, which is what "how much of my
    /// library is in English" means.
    /// </summary>
    public async Task<IReadOnlyList<LibraryBreakdownRow>> BreakdownAsync(UnitOfWork uow, long libraryId, string facet)
    {
        ArgumentNullException.ThrowIfNull(uow);
        if (!LibraryFacets.IsKnown(facet))
        {
            return [];
        }

        return await uow.QueryAsync(
            "SELECT x.value, COUNT(*), COALESCE(SUM(f.size_bytes), 0) FROM library_file_facets AS x " +
            "JOIN library_files AS f ON f.id = x.library_file_id " +
            "WHERE x.library_id = @library_id AND x.facet = @facet " +
            "GROUP BY x.value ORDER BY COUNT(*) DESC, x.value ASC",
            reader => new LibraryBreakdownRow(SqliteValues.GetString(reader, 0), reader.GetInt64(1), reader.GetInt64(2)),
            ("@library_id", libraryId),
            ("@facet", facet)).ConfigureAwait(false);
    }

    /// <summary>Every breakdown the Overview, Codecs and Languages views show, in one round trip.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<LibraryBreakdownRow>>> AllBreakdownsAsync(UnitOfWork uow, long libraryId)
    {
        var result = new Dictionary<string, IReadOnlyList<LibraryBreakdownRow>>(StringComparer.Ordinal);
        foreach (var facet in LibraryFacets.All)
        {
            result[facet] = await BreakdownAsync(uow, libraryId, facet).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>How many files match the filter, before paging — the Files table's "N files" and page count.</summary>
    public async Task<long> CountFilesAsync(UnitOfWork uow, long libraryId, LibraryFileQuery filter)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var (where, parameters) = BuildWhere(libraryId, filter);
        return await uow.CountAsync("SELECT COUNT(*) " + FromFiles + where, parameters).ConfigureAwait(false);
    }

    /// <summary>One page of the Files table. The sort is always tie-broken by path, so paging never repeats a row.</summary>
    public async Task<IReadOnlyList<LibraryFileRow>> ListFilesAsync(UnitOfWork uow, long libraryId, LibraryFileQuery filter)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(filter);
        var (where, parameters) = BuildWhere(libraryId, filter);
        var pageSize = Math.Clamp(filter.PageSize, 1, LibraryFileSort.MaxPageSize);
        var offset = (long)(Math.Max(filter.Page, 1) - 1) * pageSize;
        var direction = filter.Descending ? "DESC" : "ASC";
        var column = filter.Sort == LibraryFileSort.Status ? StatusRankSql : LibraryFileSort.ColumnFor(filter.Sort);
        var order = $"ORDER BY {column} {direction}, f.path ASC";

        return await uow.QueryAsync(
            "SELECT f.id, f.path, f.size_bytes, f.mtime, f.classification, f.summary, f.reason, f.removed_audio_tracks, " +
            "f.removed_subtitle_tracks, f.estimated_bytes_saved, f.manager_kind, f.manager_title, f.video_codec, " +
            "f.video_height, f.resolution_class, f.audio_track_count, f.subtitle_track_count, f.audio_summary, " +
            "f.subtitle_summary, f.link_count, f.problem_kind, m.cleaned_at, COALESCE(m.leave_alone, 0), " + StatusSql +
            ", f.change_reason " + FromFiles + where + " " + order +
            " LIMIT @limit OFFSET @offset",
            ReadRow,
            [.. parameters, ("@limit", (object?)pageSize), ("@offset", offset)]).ConfigureAwait(false);
    }

    /// <summary>
    /// The Problems view: every kind with at least one file, in <see cref="LibraryProblems.All"/>'s order, with a
    /// handful of paths each. <paramref name="cleanHardlinkedFiles"/> is the library's own #508 setting — when it
    /// is on, a file another name shares is deliberately allowed through, so it is not a problem to report.
    /// </summary>
    public async Task<IReadOnlyList<LibraryProblemGroup>> ProblemsAsync(UnitOfWork uow, long libraryId, bool cleanHardlinkedFiles)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var groups = new List<LibraryProblemGroup>();
        foreach (var kind in LibraryProblems.All)
        {
            if (kind == LibraryProblemKind.Seeding && cleanHardlinkedFiles)
            {
                continue;
            }

            var query = QueryFor(kind);
            var totals = await TotalsAsync(uow, libraryId, query).ConfigureAwait(false);
            if (totals.Files == 0)
            {
                continue;
            }

            var sample = await ListFilesAsync(uow, libraryId, query with { PageSize = ProblemSampleSize }).ConfigureAwait(false);
            groups.Add(new LibraryProblemGroup(kind, totals.Files, totals.SizeBytes, sample.Select(f => f.Path).ToList()));
        }

        return groups;
    }

    /// <summary>
    /// The filter that selects one problem kind. <see cref="LibraryProblemKind.Seeding"/> is the one kind read from
    /// a column rather than <c>problem_kind</c>: a link count above one is a fact about the file, recorded at every
    /// scan, while the other kinds are verdicts the scan or a clean's preflight reached. The groups never overlap:
    /// every <c>cannot_process</c> file carries the scan's own kind and lands in exactly that group, and seeding only
    /// ever holds back a file the rules would otherwise change.
    /// </summary>
    public LibraryFileQuery QueryFor(LibraryProblemKind kind) => kind == LibraryProblemKind.Seeding
        ? new LibraryFileQuery { ProblemKind = LibraryProblemKind.Seeding }
        : new LibraryFileQuery { ProblemKind = kind };

    /// <summary>The path of every file matching a filter, for a Clean over a whole filtered selection.</summary>
    public async Task<IReadOnlyList<string>> PathsAsync(UnitOfWork uow, long libraryId, LibraryFileQuery filter)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var (where, parameters) = BuildWhere(libraryId, filter);
        return await uow.QueryAsync(
            "SELECT f.path " + FromFiles + where + " ORDER BY f.path",
            reader => SqliteValues.GetString(reader, 0),
            parameters).ConfigureAwait(false);
    }

    /// <summary>
    /// Records why a clean's preflight refused to queue one file, so the #568 Problems view can group it without
    /// re-running thousands of filesystem and media-manager checks. Only ever touches a row the scan classified as
    /// something other than <c>cannot_process</c>: a scan's own verdict about an unreadable file outranks this.
    /// Passing <see langword="null"/> clears a stale note for a file that is fine now.
    /// </summary>
    public async Task RecordPreflightProblemAsync(UnitOfWork uow, long libraryId, string path, LibraryProblemKind? kind)
    {
        ArgumentNullException.ThrowIfNull(uow);
        await uow.ExecuteAsync(
            "UPDATE library_files SET problem_kind = @kind WHERE library_id = @library_id AND path = @path " +
            "AND classification <> 'cannot_process'",
            ("@kind", kind is { } value ? LibraryProblems.Name(value) : null),
            ("@library_id", libraryId),
            ("@path", path)).ConfigureAwait(false);
    }

    private static LibraryFileRow ReadRow(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        SqliteValues.GetString(reader, 1),
        SqliteValues.GetInt64(reader, 2),
        SqliteValues.GetInt64(reader, 3),
        ClassificationOf(SqliteValues.GetString(reader, 4)),
        SqliteValues.GetStringOrNull(reader, 5),
        SqliteValues.GetStringOrNull(reader, 6),
        (int)SqliteValues.GetInt64(reader, 7),
        (int)SqliteValues.GetInt64(reader, 8),
        SqliteValues.GetInt64(reader, 9),
        SqliteValues.GetStringOrNull(reader, 10),
        SqliteValues.GetStringOrNull(reader, 11),
        SqliteValues.GetStringOrNull(reader, 12) ?? LibraryFileFacts.Unknown,
        reader.IsDBNull(13) ? null : (int)reader.GetInt64(13),
        SqliteValues.GetStringOrNull(reader, 14) ?? LibraryFileFacts.Unknown,
        (int)SqliteValues.GetInt64(reader, 15),
        (int)SqliteValues.GetInt64(reader, 16),
        SqliteValues.GetStringOrNull(reader, 17),
        SqliteValues.GetStringOrNull(reader, 18),
        reader.IsDBNull(19) ? null : (int)reader.GetInt64(19),
        LibraryProblems.Parse(SqliteValues.GetStringOrNull(reader, 20)),
        TimestampColumns.Parse(reader.GetValue(21)),
        SqliteValues.GetBool(reader, 22),
        SqliteValues.GetString(reader, 23),
        SqliteValues.GetStringOrNull(reader, 24));

    private static LibraryFileClassification ClassificationOf(string value) => value switch
    {
        "matches" => LibraryFileClassification.Matches,
        "would_change" => LibraryFileClassification.WouldChange,
        _ => LibraryFileClassification.CannotProcess,
    };

    /// <summary>
    /// Builds the shared <c>WHERE</c> for every read here. Every value is a parameter and every facet name is
    /// checked against <see cref="LibraryFacets.All"/> first, so nothing a client sends is ever concatenated in.
    /// </summary>
    private static (string Where, (string Name, object? Value)[] Parameters) BuildWhere(long libraryId, LibraryFileQuery? filter)
    {
        var clauses = new List<string> { "f.library_id = @library_id" };
        var parameters = new List<(string Name, object? Value)>
        {
            ("@library_id", libraryId),
            ("@clean_kind", LibraryModeJobKinds.CleanKind),
            ("@clean_prefix", SqliteLike.Escape($"{LibraryModeJobKinds.CleanKind}:{libraryId}:") + "%"),
            ("@cleans_hardlinked", filter?.CleansHardlinkedFiles == true ? 1 : 0),
        };

        if (filter is null)
        {
            return (BuildWhereText(clauses), [.. parameters]);
        }

        if (!string.IsNullOrWhiteSpace(filter.Classification))
        {
            clauses.Add("f.classification = @classification");
            parameters.Add(("@classification", filter.Classification));
        }

        if (!string.IsNullOrWhiteSpace(filter.ManagerKind))
        {
            clauses.Add("LOWER(f.manager_kind) = LOWER(@manager_kind)");
            parameters.Add(("@manager_kind", filter.ManagerKind));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            clauses.Add("(f.path LIKE @search ESCAPE '\\' OR COALESCE(f.manager_title, '') LIKE @search ESCAPE '\\')");
            parameters.Add(("@search", "%" + SqliteLike.Escape(filter.Search) + "%"));
        }

        if (LibraryFileStatus.IsKnown(filter.Status))
        {
            clauses.Add($"{StatusSql} = @status");
            parameters.Add(("@status", filter.Status));
        }

        if (string.Equals(filter.State, "cleaned", StringComparison.Ordinal))
        {
            clauses.Add("m.cleaned_at IS NOT NULL");
        }
        else if (string.Equals(filter.State, "left_alone", StringComparison.Ordinal))
        {
            clauses.Add("COALESCE(m.leave_alone, 0) = 1");
        }

        if (filter.ProblemKind is { } problem)
        {
            if (problem == LibraryProblemKind.Seeding)
            {
                // A link count above one is the evidence; the scan records it per file, so no stat() per row here.
                // Only a file a clean would otherwise change is held back by it: a file that already matches has
                // nothing to clean, and a file the scan could not process is already in the group for that reason.
                // Keeping every group disjoint is what lets the Overview add the groups up without counting a
                // file twice (the "3 files" alert beside a "Cannot be processed 4" tile).
                clauses.Add(
                    "COALESCE(f.link_count, 1) > 1 AND f.classification = 'would_change' " +
                    "AND (f.problem_kind IS NULL OR f.problem_kind = 'seeding')");
            }
            else
            {
                clauses.Add("f.problem_kind = @problem_kind");
                parameters.Add(("@problem_kind", LibraryProblems.Name(problem)));
            }
        }

        var index = 0;
        foreach (var facet in filter.Facets)
        {
            if (!LibraryFacets.IsKnown(facet.Facet) || string.IsNullOrEmpty(facet.Value))
            {
                continue;
            }

            var facetName = "@facet" + index.ToString(CultureInfo.InvariantCulture);
            var valueName = "@facetvalue" + index.ToString(CultureInfo.InvariantCulture);
            clauses.Add(
                $"EXISTS (SELECT 1 FROM library_file_facets AS x{index} WHERE x{index}.library_file_id = f.id " +
                $"AND x{index}.facet = {facetName} AND x{index}.value = {valueName})");
            parameters.Add((facetName, facet.Facet));
            parameters.Add((valueName, facet.Value));
            index++;
        }

        return (BuildWhereText(clauses), [.. parameters]);
    }

    private static string BuildWhereText(List<string> clauses) => "WHERE " + string.Join(" AND ", clauses);
}
