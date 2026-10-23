using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing;

/// <summary>
/// SQLite access for Processing libraries and rule sets, plus the row IO <c>LibraryDiscoveryService</c> (#554)
/// needs for a discovered library's create and unlink. The reject-support gate lives in <c>RejectSupportEvaluator</c>.
/// Rule-set CRUD and its default-profile bookkeeping live in the <c>.RuleSets</c> partial.
/// </summary>
public sealed partial class LibraryStore
{
    private const string LibraryColumns =
        "id, name, enabled, media_type, display_order, watched_folder, work_folder, output_folder, " +
        "media_extensions_csv, exclude_markers_csv, include_patterns_csv, exclude_patterns_csv, min_file_size_mb, max_file_size_mb, " +
        "rejected_file_action, ready_after_seconds, created_after, created_before, modified_after, modified_before, " +
        "exclude_hidden, top_level_only, sidecar_patterns_csv, preserve_original_timestamps, output_collision_policy, " +
        "ffmpeg_strictness, scan_interval_seconds, ignore_size_changes, file_system_events_enabled, skip_access_tests, " +
        "schedule_enabled, schedule_hours_limited, schedule_days, schedule_grid, schedule_start, schedule_end, max_attempts, " +
        "retry_backoff_seconds, retry_execution_failures, retry_preflight_failures, failure_policy, max_concurrent_files, " +
        "priority, rule_set_id, discovered_from_connection_id, discovered_library_key, created_at, updated_at, " +
        // #548: appended, not inserted - ReadLibrary reads by position.
        "remux_writer, remove_original_after_success, minimum_free_disk_space_mb";

    private readonly DataChangePublisher? _changes;

    /// <param name="changes">Told, once a write commits, that the workflows changed, so every open screen reads them again.</param>
    public LibraryStore(DataChangePublisher? changes = null)
    {
        _changes = changes;
    }

    public async Task<List<ProcessingLibraryRecord>> ListAsync(UnitOfWork uow, bool enabledOnly = false)
    {
        var sql = $"SELECT {LibraryColumns} FROM libraries" + (enabledOnly ? " WHERE enabled = 1" : string.Empty) +
                  " ORDER BY display_order, id";
        return await uow.QueryAsync(sql, ReadLibrary).ConfigureAwait(false);
    }

    public Task<ProcessingLibraryRecord?> GetAsync(UnitOfWork uow, long id) =>
        uow.QuerySingleAsync($"SELECT {LibraryColumns} FROM libraries WHERE id = @id", ReadLibrary, ("@id", id));

    public Task<ProcessingLibraryRecord?> GetByNameAsync(UnitOfWork uow, string name) =>
        uow.QuerySingleAsync($"SELECT {LibraryColumns} FROM libraries WHERE name = @name", ReadLibrary, ("@name", name));

    /// <summary>The first library covering a scope, in display order.</summary>
    public Task<ProcessingLibraryRecord?> SeededForScopeAsync(UnitOfWork uow, string mediaScope) =>
        uow.QuerySingleAsync(
            $"SELECT {LibraryColumns} FROM libraries WHERE media_type = @scope ORDER BY display_order, id LIMIT 1",
            ReadLibrary, ("@scope", ProcessingMediaScopes.Normalize(mediaScope)));

    public async Task<List<long>> ManagerConnectionIdsAsync(UnitOfWork uow, long libraryId)
    {
        var rows = await uow.QueryAsync(
            "SELECT connection_id FROM library_manager_links WHERE library_id = @id ORDER BY connection_id",
            reader => reader.GetInt64(0), ("@id", libraryId)).ConfigureAwait(false);
        return rows;
    }

    /// <summary>
    /// The kinds of the media managers one workflow is linked to, read from the same <c>library_manager_links</c> rows the
    /// editor writes. Every rule about originals and scanning asks this, so there is one answer to "is it linked, and to what".
    /// </summary>
    public async Task<WorkflowManagerLinks> ManagerLinksAsync(UnitOfWork uow, long libraryId)
    {
        var kinds = await uow.QueryAsync(
            "SELECT media_manager_connections.kind FROM library_manager_links " +
            "JOIN media_manager_connections ON media_manager_connections.id = library_manager_links.connection_id " +
            "WHERE library_manager_links.library_id = @id ORDER BY library_manager_links.connection_id",
            reader => reader.GetString(0), ("@id", libraryId)).ConfigureAwait(false);
        return new WorkflowManagerLinks(kinds);
    }

    /// <summary>The ids of the workflows a linked Deluno hands downloads to, which Weir's own scan never feeds.</summary>
    public async Task<HashSet<long>> HandedOffLibraryIdsAsync(UnitOfWork uow)
    {
        var ids = await uow.QueryAsync(
            "SELECT DISTINCT library_manager_links.library_id FROM library_manager_links " +
            "JOIN media_manager_connections ON media_manager_connections.id = library_manager_links.connection_id " +
            "WHERE lower(trim(media_manager_connections.kind)) = 'deluno'",
            reader => reader.GetInt64(0)).ConfigureAwait(false);
        return [.. ids];
    }

    /// <summary>The libraries linked to one connection, in <c>display_order</c> then id (the reverse of <see cref="ManagerConnectionIdsAsync"/>).</summary>
    public async Task<List<ProcessingLibraryRecord>> LibrariesForConnectionIdAsync(UnitOfWork uow, long connectionId)
    {
        var ids = await uow.QueryAsync(
            "SELECT library_id FROM library_manager_links WHERE connection_id = @id",
            reader => reader.GetInt64(0), ("@id", connectionId)).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return [];
        }

        var wanted = ids.ToHashSet();
        return [.. (await ListAsync(uow).ConfigureAwait(false)).Where(library => wanted.Contains(library.Id))];
    }

    public async Task<HashSet<long>> KnownConnectionIdsAsync(UnitOfWork uow, IReadOnlyList<long> ids)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var placeholders = string.Join(",", ids.Select((_, i) => $"@id{i}"));
        var parameters = ids.Select((id, i) => ($"@id{i}", (object?)id)).ToArray();
        var rows = await uow.QueryAsync(
            $"SELECT id FROM media_manager_connections WHERE id IN ({placeholders})",
            reader => reader.GetInt64(0), parameters).ConfigureAwait(false);
        return [.. rows];
    }

    /// <summary>How many queued or leased Processing jobs belong to this library.</summary>
    public async Task<int> ActiveJobCountAsync(UnitOfWork uow, ProcessingLibraryRecord library)
    {
        var libraries = await ListAsync(uow).ConfigureAwait(false);
        var seededForScope = libraries.FirstOrDefault(row => row.MediaType == library.MediaType);
        var isSeeded = seededForScope is not null && seededForScope.Id == library.Id;

        var payloads = await uow.QueryAsync(
            "SELECT payload_json FROM jobs WHERE status IN ('pending', 'leased')",
            reader => reader.IsDBNull(0) ? null : reader.GetString(0)).ConfigureAwait(false);

        var count = 0;
        foreach (var raw in payloads)
        {
            if (raw is null)
            {
                continue;
            }

            WireValue data;
            try
            {
                data = WireJsonParser.Parse(raw);
            }
            catch (WireJsonDecodeException)
            {
                continue;
            }

            if (data is not WireObject dict)
            {
                continue;
            }

            if (dict.TryGetValue("library_id", out var libraryIdValue) && libraryIdValue is WireInteger libraryIdInt)
            {
                if (libraryIdInt.Value == library.Id)
                {
                    count++;
                }

                continue;
            }

            if (isSeeded)
            {
                var scope = dict.TryGetValue("media_scope", out var scopeValue) && scopeValue is WireString scopeStr
                    ? ProcessingMediaScopes.Normalize(scopeStr.Value)
                    : ProcessingMediaScopes.Movie;
                if (scope == library.MediaType)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>"Files at once" in Setup › Performance › Speed; one when the settings row does not exist yet.</summary>
    private async Task<long> FilesAtOnceAsync(UnitOfWork uow) =>
        await uow.ScalarAsync("SELECT max_concurrent_files FROM operator_settings WHERE id = 1").ConfigureAwait(false) is { } total and not DBNull
            ? Convert.ToInt64(total, System.Globalization.CultureInfo.InvariantCulture)
            : 1;

    public async Task<ProcessingLibraryRecord> CreateAsync(UnitOfWork uow, ProcessingLibraryInput body, string? weirHome = null)
    {
        var existingNames = (await ListAsync(uow).ConfigureAwait(false)).Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
        var name = LibraryRules.ValidateName(body.Name, existingNames);
        var scope = LibraryRules.ValidateScope(body.MediaType);
        var highestOrder = await uow.ScalarAsync("SELECT MAX(display_order) FROM libraries").ConfigureAwait(false);
        var displayOrder = (highestOrder is null or DBNull ? 0 : Convert.ToInt64(highestOrder, System.Globalization.CultureInfo.InvariantCulture)) + 1;

        LibraryRules.ValidateMaxConcurrentFiles(body.MaxConcurrentFiles, await FilesAtOnceAsync(uow).ConfigureAwait(false));
        var ruleSetExists = body.RuleSetId is { } wantedRuleSet && await GetRuleSetAsync(uow, wantedRuleSet).ConfigureAwait(false) is not null;
        var others = await OtherFoldersAsync(uow, excludeId: null).ConfigureAwait(false);
        var row = LibraryRules.ApplyFields(new ProcessingLibraryRecord { Name = name, MediaType = scope, DisplayOrder = displayOrder }, body, ruleSetExists);
        LibraryRules.ValidateFolders(row.WatchedFolder, row.WorkFolder, row.OutputFolder, others, weirHome);

        // A library made without a profile gets its kind's default, so no library runs on rules nobody can see.
        if (row.RuleSetId is null)
        {
            row = row with { RuleSetId = await DefaultProfileIdAsync(uow, scope).ConfigureAwait(false) };
        }

        await InsertAsync(uow, row).ConfigureAwait(false);
        var created = await GetByNameAsync(uow, name).ConfigureAwait(false) ?? throw new InvalidOperationException("Workflow insert race.");
        await SetManagerLinksAsync(uow, created.Id, body.ManagerConnectionIds).ConfigureAwait(false);
        return created;
    }

    public async Task<ProcessingLibraryRecord> UpdateAsync(UnitOfWork uow, ProcessingLibraryRecord existing, ProcessingLibraryInput body, string? weirHome = null)
    {
        var existingNames = (await ListAsync(uow).ConfigureAwait(false))
            .Where(l => l.Id != existing.Id).Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
        var name = LibraryRules.ValidateName(body.Name, existingNames);
        var scope = LibraryRules.ValidateScope(body.MediaType);
        // Only a number someone is changing is held to the total: a workflow already above it, because Files at once was
        // lowered, still saves its other settings, and shows that it is limited.
        if (body.MaxConcurrentFiles != existing.MaxConcurrentFiles)
        {
            LibraryRules.ValidateMaxConcurrentFiles(body.MaxConcurrentFiles, await FilesAtOnceAsync(uow).ConfigureAwait(false));
        }

        var ruleSetExists = body.RuleSetId is { } wantedRuleSet && await GetRuleSetAsync(uow, wantedRuleSet).ConfigureAwait(false) is not null;
        var others = await OtherFoldersAsync(uow, excludeId: existing.Id).ConfigureAwait(false);
        var row = LibraryRules.ApplyFields(existing with { Name = name, MediaType = scope }, body, ruleSetExists);
        LibraryRules.ValidateFolders(row.WatchedFolder, row.WorkFolder, row.OutputFolder, others, weirHome);

        await UpdateRowAsync(uow, row).ConfigureAwait(false);
        var updated = await GetAsync(uow, existing.Id).ConfigureAwait(false) ?? throw new InvalidOperationException("Workflow disappeared during update.");
        await SetManagerLinksAsync(uow, updated.Id, body.ManagerConnectionIds).ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// Creates a library imported from a media manager and links it to that manager as it is created, so Weir can
    /// reject a bad download through it (#651).
    /// </summary>
    /// <remarks>
    /// The row is built from the manager's own descriptor rather than an operator's request body, so
    /// <c>LibraryRules.ApplyFields</c>/<c>ValidateFolders</c> (folder-overlap and full-field validation) does not run.
    /// Columns the descriptor does not set keep their schema defaults, mirrored by
    /// <see cref="ProcessingLibraryRecord"/>'s own property defaults.
    /// </remarks>
    public async Task<ProcessingLibraryRecord> CreateDiscoveredAsync(UnitOfWork uow, ProcessingLibraryRecord row)
    {
        if (row.RuleSetId is null)
        {
            row = row with { RuleSetId = await DefaultProfileIdAsync(uow, row.MediaType).ConfigureAwait(false) };
        }

        await InsertAsync(uow, row).ConfigureAwait(false);
        var created = await GetByNameAsync(uow, row.Name).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Discovered workflow insert race.");
        if (row.DiscoveredFromConnectionId is { } connectionId)
        {
            await SetManagerLinksAsync(uow, created.Id, [connectionId]).ConfigureAwait(false);
        }

        return created;
    }

    /// <summary>Forgets where a library came from, keeping the library itself untouched.</summary>
    public async Task<ProcessingLibraryRecord> UnlinkAsync(UnitOfWork uow, ProcessingLibraryRecord row)
    {
        await uow.ExecuteAsync(
            "UPDATE libraries SET discovered_from_connection_id = NULL, discovered_library_key = NULL, " +
            "updated_at = CURRENT_TIMESTAMP WHERE id = @id",
            ("@id", row.Id)).ConfigureAwait(false);
        Changed(uow);
        return await GetAsync(uow, row.Id).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Workflow disappeared during unlink.");
    }

    public async Task DeleteAsync(UnitOfWork uow, ProcessingLibraryRecord row)
    {
        var active = await ActiveJobCountAsync(uow, row).ConfigureAwait(false);
        if (active > 0)
        {
            throw new ProcessingLibraryException(
                $"{row.Name} still has {active} job{(active == 1 ? string.Empty : "s")} queued or running. " +
                "Wait for them to finish, or cancel them, before removing the workflow — they resolve their folders from it.");
        }

        await uow.ExecuteAsync("DELETE FROM libraries WHERE id = @id", ("@id", row.Id)).ConfigureAwait(false);
        Changed(uow);
    }

    public async Task<List<ProcessingLibraryRecord>> ReorderAsync(UnitOfWork uow, IReadOnlyList<long> orderedIds)
    {
        var rows = (await ListAsync(uow).ConfigureAwait(false)).ToDictionary(r => r.Id);
        var unknown = orderedIds.Where(id => !rows.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
        {
            throw new ProcessingLibraryException($"No workflow with id {unknown[0]}.");
        }

        if (orderedIds.Distinct().Count() != rows.Count)
        {
            throw new ProcessingLibraryException("Reordering must list every workflow exactly once.");
        }

        for (var position = 0; position < orderedIds.Count; position++)
        {
            await uow.ExecuteAsync(
                "UPDATE libraries SET display_order = @order WHERE id = @id",
                ("@order", (long)position), ("@id", orderedIds[position])).ConfigureAwait(false);
        }

        Changed(uow);

        return await ListAsync(uow).ConfigureAwait(false);
    }

    public async Task SetManagerLinksAsync(UnitOfWork uow, long libraryId, IReadOnlyList<long> connectionIds)
    {
        var known = await KnownConnectionIdsAsync(uow, connectionIds).ConfigureAwait(false);
        var wanted = LibraryRules.ValidateManagerConnections(connectionIds, known);
        var existing = (await ManagerConnectionIdsAsync(uow, libraryId).ConfigureAwait(false)).ToHashSet();
        foreach (var stale in existing.Except(wanted))
        {
            await uow.ExecuteAsync(
                "DELETE FROM library_manager_links WHERE library_id = @lib AND connection_id = @conn",
                ("@lib", libraryId), ("@conn", stale)).ConfigureAwait(false);
            Changed(uow);
        }

        foreach (var added in wanted.Except(existing))
        {
            await uow.ExecuteAsync(
                "INSERT INTO library_manager_links (library_id, connection_id) VALUES (@lib, @conn)",
                ("@lib", libraryId), ("@conn", added)).ConfigureAwait(false);
            Changed(uow);
        }
    }

    /// <summary>
    /// Removes a connection's link to every workflow that has one and returns those workflows. A workflow left with no manager
    /// at all is switched off: it would otherwise scan its watched folder as Weir's own and may remove the originals the
    /// download client is still seeding, so it waits until the person has chosen what it does next. The workflows, their
    /// settings and their folders are not otherwise touched.
    /// </summary>
    public async Task<List<ProcessingLibraryRecord>> UnlinkConnectionAsync(UnitOfWork uow, long connectionId)
    {
        var linked = await LibrariesForConnectionIdAsync(uow, connectionId).ConfigureAwait(false);
        foreach (var library in linked)
        {
            await uow.ExecuteAsync(
                "DELETE FROM library_manager_links WHERE library_id = @lib AND connection_id = @conn",
                ("@lib", library.Id), ("@conn", connectionId)).ConfigureAwait(false);
            if ((await ManagerConnectionIdsAsync(uow, library.Id).ConfigureAwait(false)).Count == 0)
            {
                await uow.ExecuteAsync(
                    "UPDATE libraries SET enabled = 0, updated_at = CURRENT_TIMESTAMP WHERE id = @id", ("@id", library.Id)).ConfigureAwait(false);
            }
        }

        if (linked.Count > 0)
        {
            Changed(uow);
        }

        return linked;
    }

    private void Changed(UnitOfWork uow) => _changes?.PublishOnCommit(uow, DataTopics.Libraries);

    /// <summary>The watched and output folders of every library but <paramref name="excludeId"/>, for the overlap check.</summary>
    public async Task<List<OtherLibraryFolders>> OtherFoldersAsync(UnitOfWork uow, long? excludeId)
    {
        var rows = await ListAsync(uow).ConfigureAwait(false);
        return [.. rows.Where(r => r.Id != excludeId).Select(r => new OtherLibraryFolders(r.Id, r.Name, r.WatchedFolder, r.OutputFolder))];
    }
}
