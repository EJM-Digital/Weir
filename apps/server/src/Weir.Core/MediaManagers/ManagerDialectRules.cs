using System.Globalization;
using Weir.Core.Json;

namespace Weir.Core.MediaManagers;

/// <summary>
/// The phrasing half of the manager dialects: each product's JSON read into the neutral
/// <see cref="IMediaManagerPort"/> shapes. The HTTP half lives in Weir.Infrastructure.
/// </summary>
public static class ManagerDialectRules
{
    public const string ExternalManifestPath = "/api/integrations/external/manifest";
    public const string ExternalQueuePath = "/api/integrations/external/queue";

    /// <summary>
    /// Deluno's "a tool changed this library file" endpoint (Deluno#528, confirmed against the
    /// checkout at commit 177ff18a: <c>src/Deluno.Worker/ExternalFileChangedEndpoints.cs:43</c>). Body is
    /// <c>{"path": "...", "reason": "...", "tool": "Weir"}</c>; it answers 202 Accepted, not one of the
    /// three statuses every other manager call accepts (#507).
    /// </summary>
    public const string ExternalFileChangedPath = "/api/integrations/external/file-changed";

    /// <summary>
    /// The manifest capability Deluno advertises for <see cref="ExternalFileChangedPath"/>
    /// (<c>ExternalIntegrationEndpointRouteBuilderExtensions.cs:108</c>): gate the call on it rather than
    /// trying and classifying a 404, since an older Deluno without #528 has no route there at all.
    /// </summary>
    public const string ExternalFileChangedCapability = "external-file-changed";

    public const int ArrLibraryPageSize = 200_000;
    public const int ArrQueuePageSize = 1000;

    public static readonly TimeSpan QueueTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan LibraryTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan DescribeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Deluno asks each download client in turn, allowing each 8 seconds, so its answer can take longer than a manifest.</summary>
    public static readonly TimeSpan DownloadDestinationsTimeout = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> SettledStates = new(StringComparer.Ordinal)
    {
        "completed", "complete", "done", "finished", "succeeded", "success", "ok", "failed", "failure", "error",
        "cancelled", "canceled", "aborted", "skipped", "ignored", "rejected",
    };

    private static readonly HashSet<string> ImportPendingStates = new(StringComparer.Ordinal)
    {
        "importing", "importpending", "import_pending", "import-pending", "finalizing", "finalising", "moving", "handoff", "handing_off",
    };

    /// <summary>The scope a manager's media type names, accepting plural spellings too; null when it names neither.</summary>
    public static string? ScopeFromMediaType(WireValue? raw)
    {
        var value = (ManagerValues.Text(raw) ?? string.Empty).ToLowerInvariant();
        return value switch
        {
            "movie" or "movies" or "film" or "films" => MediaManagerKinds.Movie,
            "tv" or "series" or "show" or "shows" or "episode" or "episodes" => MediaManagerKinds.Tv,
            _ => null,
        };
    }

    /// <summary>
    /// One sentence an operator can act on, with the connection named. It never quotes the exception, an HTTP status or
    /// a transport reason: those stay on the exception for the logs.
    /// </summary>
    public static string Unreachable(ManagerConnection connection, Exception exception, string what)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(exception);
        switch (exception)
        {
            case MediaManagerRateLimitedException limited:
                var wait = limited.RetryAfterSeconds;
                var when = wait is { } seconds && seconds != 0
                    ? $" It asked Weir to wait about {Math.Truncate(seconds).ToString("0", CultureInfo.InvariantCulture)}s."
                    : string.Empty;
                return $"{connection.Label} is rate limiting Weir, so it could not say {what}.{when} " +
                       "Weir backed off rather than retrying straight away.";
            case MediaManagerRedirectedException redirected:
                return $"{connection.Label} {redirected.Message}";
            case MediaManagerHttpException { InnerException: WireValueException }:
                return ConnectionUnreachableText.For(connection.Label, connection.BaseUrl);
            case MediaManagerHttpException http:
                var detail = http.Message;
                if (detail.Contains("HTTP 401", StringComparison.Ordinal) || detail.Contains("HTTP 403", StringComparison.Ordinal))
                {
                    return $"{connection.Label} refused Weir's API key, so it could not say {what}. " +
                           "Check the key in Setup › Connections › Media managers and save it again.";
                }

                return $"{connection.Label} did not give Weir the answer it expected when asked {what}. " +
                       $"Check the address points at {connection.Label} itself, not a page inside it, and that {connection.Label} is running normally.";
            default:
                return ConnectionUnreachableText.For(connection.Label, connection.BaseUrl);
        }
    }

    /// <summary>
    /// The root folder paths. A root folder whose <c>path</c> is JSON <c>null</c>, blank or missing is skipped
    /// (#544 item 4) rather than given a label the manager did not name, the same as the <c>libraries</c> side of
    /// <see cref="ArrRootFolders"/>.
    /// </summary>
    public static List<string> ArrRootPaths(IEnumerable<WireObject> rows) =>
        [.. rows.Select(row => ManagerValues.Text(row.Get("path"))).OfType<string>()];

    /// <summary>The arr <c>describe</c> answer from a <c>/api/v3/rootfolder</c> payload.</summary>
    public static (List<string> Roots, List<ManagerLibraryDescriptor> Libraries) ArrRootFolders(WireValue? payload, string scope)
    {
        var rows = ManagerValues.Dicts(payload);
        var libraries = new List<ManagerLibraryDescriptor>();
        foreach (var row in rows)
        {
            if (ManagerValues.Text(row.Get("path")) is not { } path)
            {
                continue;
            }

            var id = ManagerValues.FirstNumber(row, "id");
            var key = id is { } number && !number.IsZero ? number.ToString(CultureInfo.InvariantCulture) : path;
            libraries.Add(new ManagerLibraryDescriptor(key, path, scope, path));
        }

        return (ArrRootPaths(rows), libraries);
    }

    /// <summary>The arr queue rows: a list, or the <c>records</c> of a paged envelope.</summary>
    public static List<ManagerQueueRow> ArrQueueRows(WireValue? payload, string scope)
    {
        WireValue? records = payload switch
        {
            WireArray => payload,
            null or WireNull => null,
            WireObject dict => dict.Get("records"),
            // Any other truthy shape is not a queue answer: throw, and the caller answers with a 500.
            _ when !payload.IsTruthy => null,
            _ => throw new InvalidOperationException("The media manager's queue answer was neither a list nor an object with records."),
        };
        return [.. ManagerValues.Dicts(records).Select(row => new ManagerQueueRow(scope, row))];
    }

    /// <summary>Library file paths from <c>/api/v3/movie</c> (nested under <paramref name="fileKey"/>) or <c>/api/v3/episodefile</c>.</summary>
    public static List<string> ArrLibraryFilePaths(WireValue? payload, string? fileKey)
    {
        var paths = new List<string>();
        foreach (var row in ManagerValues.Dicts(payload))
        {
            var holder = fileKey is null ? row : row.Get(fileKey);
            if (holder is WireObject mapping && ManagerValues.Text(mapping.Get("path")) is { } found)
            {
                paths.Add(found);
            }
        }

        return paths;
    }

    /// <summary>
    /// Library files (#507), the movie half: <c>/api/v3/movie</c> rows carry the title's own id/name and,
    /// when the movie has a file, the embedded <c>movieFile.path</c> (verified: <c>MovieResource</c> and
    /// <c>MovieFileResource</c> in Radarr's <c>openapi.json</c> — a movie with no file simply has no
    /// <c>movieFile</c>, which is skipped rather than treated as a match).
    /// </summary>
    public static List<ManagerLibraryFile> ArrMovieLibraryFiles(WireValue? payload)
    {
        var files = new List<ManagerLibraryFile>();
        foreach (var row in ManagerValues.Dicts(payload))
        {
            if (ManagerValues.FirstNumber(row, "id") is not { } id)
            {
                continue;
            }

            if (row.Get("movieFile") is not WireObject file || ManagerValues.Text(file.Get("path")) is not { } path)
            {
                continue;
            }

            var idText = id.ToString(CultureInfo.InvariantCulture);
            var fileId = ManagerValues.FirstNumber(file, "id") is { } fileNumber ? (long)fileNumber : (long?)null;
            var qualityProfileId = ManagerValues.FirstNumber(row, "qualityProfileId") is { } profileNumber ? (long)profileNumber : (long?)null;
            files.Add(new ManagerLibraryFile(idText, ManagerValues.FirstText(row, "title") ?? idText, path, fileId, qualityProfileId));
        }

        return files;
    }

    /// <summary>
    /// Library files (#507), the series half: one series' <c>/api/v3/episodefile?seriesId=</c> rows, each
    /// tagged with the series id/title/quality-profile-id the caller already looked up from <c>/api/v3/series</c>
    /// (verified: Sonarr's <c>EpisodeFileController.GetEpisodeFiles</c> throws <c>BadRequestException</c> without
    /// <c>seriesId</c> or <c>episodeFileIds</c>, so listing every file means walking <c>/api/v3/series</c> first,
    /// one call per series). #551's <see cref="ManagerLibraryFile.FileId"/> is each row's own <c>id</c> — the
    /// individual episode file, not the series.
    /// </summary>
    public static List<ManagerLibraryFile> ArrEpisodeLibraryFiles(WireValue? payload, string seriesId, string seriesTitle, long? seriesQualityProfileId = null)
    {
        var files = new List<ManagerLibraryFile>();
        foreach (var row in ManagerValues.Dicts(payload))
        {
            if (ManagerValues.Text(row.Get("path")) is { } path)
            {
                var fileId = ManagerValues.FirstNumber(row, "id") is { } fileNumber ? (long)fileNumber : (long?)null;
                files.Add(new ManagerLibraryFile(seriesId, seriesTitle, path, fileId, seriesQualityProfileId));
            }
        }

        return files;
    }

    /// <summary>
    /// The file-changed (#507) <c>POST /api/v3/command</c> shape: the command's own name (matched
    /// case-insensitively against its class name minus "Command" — <c>CommandController.StartCommand</c>) and
    /// its one id property, camelCase (<c>STJson</c>'s <c>JsonNamingPolicy.CamelCase</c>). Verified against
    /// <c>RescanMovieCommand</c>/<c>RescanSeriesCommand</c> in each product's <c>MediaFiles/Commands</c>.
    /// </summary>
    public static (string CommandName, string IdProperty) ArrRescanCommand(string mediaScope) =>
        mediaScope == MediaManagerKinds.Tv ? ("RescanSeries", "seriesId") : ("RescanMovie", "movieId");

    /// <summary>The manifest's libraries, from the first non-empty list among its known keys, or a bare list.</summary>
    public static List<WireObject> ManifestLibraries(WireValue? payload)
    {
        if (payload is WireObject mapping)
        {
            foreach (var key in new[] { "libraries", "roots", "rootFolders", "root_folders", "items" })
            {
                var found = ManagerValues.Dicts(mapping.Get(key));
                if (found.Count > 0)
                {
                    return found;
                }
            }

            return [];
        }

        return ManagerValues.Dicts(payload);
    }

    /// <summary>
    /// The manifest's <c>downloadClients</c>: each client's name, whether it is enabled and the category it files each media type
    /// under (Deluno's <c>ExternalDownloadClientManifest</c>). It carries no save folder.
    /// </summary>
    public static List<ManagerDownloadClientDescriptor> ManifestDownloadClients(WireValue? payload) =>
        payload is WireObject mapping
            ? [.. ManagerValues.Dicts(mapping.Get("downloadClients"))
                .Where(client => ManagerValues.FirstText(client, "name") is not null)
                .Select(client => new ManagerDownloadClientDescriptor(
                    ManagerValues.FirstText(client, "name")!,
                    client.Get("isEnabled") is not WireBool { Value: false },
                    ManagerValues.FirstText(client, "moviesCategory"),
                    ManagerValues.FirstText(client, "tvCategory")))]
            : [];

    /// <summary>The manifest's capability strings, lower-cased.</summary>
    public static IReadOnlySet<string> ManifestCapabilities(WireValue? payload)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        if (payload is WireObject mapping && mapping.Get("capabilities") is WireArray list)
        {
            foreach (var item in list.Items.OfType<WireString>())
            {
                var text = WireStrings.Strip(item.Value);
                if (text.Length > 0)
                {
                    set.Add(text.ToLowerInvariant());
                }
            }
        }

        return set;
    }

    /// <summary>A library's id (<c>id</c> in the manifest, <c>libraryId</c> in the download destinations), numeric or text.</summary>
    public static string? ManifestLibraryKey(WireObject library, string idField = "id")
    {
        ArgumentNullException.ThrowIfNull(library);
        var number = ManagerValues.FirstNumber(library, idField);
        return number is { } n ? n.ToString(CultureInfo.InvariantCulture) : ManagerValues.FirstText(library, idField);
    }

    /// <summary>A manifest library, read against the confirmed Deluno contract.</summary>
    public static ManagerLibraryDescriptor ManifestLibraryDescriptor(WireObject library)
    {
        ArgumentNullException.ThrowIfNull(library);
        var key = ManifestLibraryKey(library) ?? string.Empty;
        var name = ManagerValues.FirstText(library, "name") ?? key;
        var scope = ScopeFromMediaType(library.Get("mediaType"));
        var root = ManagerValues.FirstText(library, "rootPath");
        var workflow = WireStrings.Strip(ManagerValues.Text(library.Get("importWorkflow")) ?? string.Empty).ToLowerInvariant();
        var processesBeforeImport = workflow == "refine-before-import";
        var output = processesBeforeImport ? ManagerValues.FirstText(library, "processorOutputPath") : null;
        // Deluno's ExternalLibraryManifest carries DownloadsPath beside RootPath (Deluno
        // src/Deluno.Platform/ExternalIntegrationEndpointRouteBuilderExtensions.cs, ExternalLibraryManifest): the folder
        // the library's downloads arrive in, which is where a hand-off's sourcePath comes from.
        var downloads = ManagerValues.FirstText(library, "downloadsPath");
        return new ManagerLibraryDescriptor(key, name, scope, root, output, processesBeforeImport, downloads);
    }

    /// <summary>The external-integration <c>describe</c> answer from a manifest payload.</summary>
    public static ManagerDescription ExternalDescription(ManagerConnection connection, ManagerCapabilities staticCapabilities, WireValue? payload)
    {
        ArgumentNullException.ThrowIfNull(staticCapabilities);
        var libraries = ManifestLibraries(payload);
        var scopes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var library in libraries)
        {
            if (ScopeFromMediaType(ManagerValues.Or(library.Get("mediaType"), library.Get("media_type"), library.Get("scope"))) is { } scope)
            {
                scopes.Add(scope);
            }
        }

        var roots = libraries
            .Select(library => WireStrings.Strip(ManagerValues.FirstText(library, "path", "rootFolder", "root_folder", "root") ?? string.Empty))
            .Where(path => path.Length > 0)
            .ToList();
        var capabilities = scopes.Count > 0
            ? new ManagerCapabilities(scopes, staticCapabilities.ReportsQueue, staticCapabilities.ReportsLibraryTruth, staticCapabilities.Summary)
            : staticCapabilities;
        return new ManagerDescription(
            connection,
            SignalStatus.Reported,
            capabilities,
            roots,
            [.. libraries.Where(library => !string.IsNullOrEmpty(ManifestLibraryKey(library))).Select(ManifestLibraryDescriptor)],
            AdvertisedCapabilities: ManifestCapabilities(payload),
            DownloadClients: ManifestDownloadClients(payload));
    }

    /// <summary>Every queue row, whichever container the manager wrapped it in.</summary>
    public static List<WireObject> ExternalQueueEntries(WireValue? payload)
    {
        if (payload is WireArray)
        {
            return ManagerValues.Dicts(payload);
        }

        if (payload is not WireObject mapping)
        {
            return [];
        }

        var collected = new List<WireObject>();
        foreach (var key in new[] { "jobs", "dispatches", "downloads", "queue", "items", "records", "results" })
        {
            collected.AddRange(ManagerValues.Dicts(mapping.Get(key)));
        }

        return collected;
    }

    /// <summary>A queue row's status; an unrecognised state reads as still in progress.</summary>
    public static string ExternalQueueStatus(WireObject entry)
    {
        var raw = ManagerValues.FirstText(entry, "status", "state", "jobStatus", "job_status", "phase") ?? string.Empty;
        var value = raw.ToLowerInvariant().Replace(" ", "_", StringComparison.Ordinal);
        if (ImportPendingStates.Contains(value))
        {
            return "importpending";
        }

        return SettledStates.Contains(value) ? value : "downloading";
    }

    /// <summary>An external queue row in the neutral shape; null when the row names no scope.</summary>
    public static ManagerQueueRow? ExternalQueueRow(WireObject entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var scope = ScopeFromMediaType(ManagerValues.Or(
            entry.Get("mediaType"), entry.Get("media_type"), entry.Get("mediaScope"), entry.Get("media_scope"), entry.Get("scope")));
        if (scope is null)
        {
            return null;
        }

        var path = ManagerValues.FirstText(entry, "outputPath", "output_path", "targetPath", "target_path", "sourcePath", "source_path", "filePath", "file_path", "path");
        var title = ManagerValues.FirstText(entry, "title", "releaseName", "release_name", "name");
        var payload = new WireObject()
            .Set("status", ExternalQueueStatus(entry))
            .Set("outputPath", path)
            .Set("title", title)
            .Set("media", new WireObject().Set("title", title).Set("year", ManagerValues.Number(ManagerValues.FirstNumber(entry, "year", "releaseYear", "release_year"))))
            .Set("entityId", ManagerValues.Number(ManagerValues.FirstNumber(entry, "entityId", "entity_id", "mediaId", "media_id", "id")));
        return new ManagerQueueRow(scope, payload);
    }

    /// <summary>Drops rows describing the other kind of library; a silent manager stays silent.</summary>
    public static ManagerQueueSignal OnlyRowsForScope(ManagerQueueSignal signal, string mediaScope)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (!signal.IsReported)
        {
            return signal;
        }

        var kept = signal.Rows.Where(row => row.Scope == mediaScope).ToList();
        return kept.Count == signal.Rows.Count ? signal : signal with { Rows = kept };
    }
}
