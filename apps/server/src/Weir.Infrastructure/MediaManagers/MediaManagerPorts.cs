using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Configuration;
using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>The outbound dialect for a kind, or null for a kind Weir does not know.</summary>
public interface IMediaManagerPorts
{
    IMediaManagerPort? PortForKind(string? kind);
}

/// <summary>The manager ports over HTTP, one per known kind.</summary>
public sealed class HttpMediaManagerPorts : IMediaManagerPorts
{
    private readonly IManagerHttpHandlerFactory _handlers;
    private readonly ILogger _logger;

    public HttpMediaManagerPorts(IManagerHttpHandlerFactory handlers, ILogger<HttpMediaManagerPorts>? logger = null)
    {
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _logger = logger ?? NullLogger<HttpMediaManagerPorts>.Instance;
    }

    public IMediaManagerPort? PortForKind(string? kind) =>
        ManagerKindProfiles.ForKind(kind) is { } profile ? new HttpMediaManagerPort(profile, _handlers, _logger) : null;

    /// <summary>
    /// A connection from the <c>WEIR_ARR_*</c> credentials that predate the connections table, or null when unset.
    /// </summary>
    public static ManagerConnection? EnvironmentConnectionForScope(WeirOptions options, string mediaScope)
    {
        ArgumentNullException.ThrowIfNull(options);
        var (url, key, kind) = mediaScope == MediaManagerKinds.Tv
            ? (options.ArrSonarrBaseUrl, options.ArrSonarrApiKey, "sonarr")
            : (options.ArrRadarrBaseUrl, options.ArrRadarrApiKey, "radarr");
        return string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key)
            ? null
            : new ManagerConnection(kind, "from environment", url, key);
    }
}

/// <summary>One manager over HTTP; the kind's profile chooses the Sonarr/Radarr v3 dialect or the external-integration one.</summary>
public sealed partial class HttpMediaManagerPort : IMediaManagerPort
{
    private readonly ManagerKindProfile _profile;
    private readonly IManagerHttpHandlerFactory _handlers;
    private readonly ILogger _logger;

    public HttpMediaManagerPort(ManagerKindProfile profile, IManagerHttpHandlerFactory handlers, ILogger? logger = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The plain sentence for a manager that did not answer; the technical reason goes to the log only.</summary>
    private string UnreachableText(ManagerConnection connection, Exception exception, string what)
    {
        LogManagerDidNotAnswer(_logger, exception, connection.Label, what);
        return ManagerDialectRules.Unreachable(connection, exception, what);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Label} did not answer when Weir asked {What}.")]
    private static partial void LogManagerDidNotAnswer(ILogger logger, Exception error, string label, string what);

    public string Kind => _profile.Kind;

    public ManagerCapabilities Capabilities() => _profile.Capabilities;

    /// <summary>Whether a failed call may succeed when made again: the manager did not answer, or answered that it is busy or failing. A refusal or an answer Weir cannot read will not change.</summary>
    private static bool MayAnswerLater(Exception exception) =>
        exception is MediaManagerUnreachableException or MediaManagerRateLimitedException or MediaManagerHttpException { StatusCode: >= 500 or 408 };

    public async Task<ManagerDescription> DescribeAsync(ManagerConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var capabilities = Capabilities();
        var path = _profile.IsArr ? "/api/v3/rootfolder" : ManagerDialectRules.ExternalManifestPath;
        WireValue? payload;
        try
        {
            payload = await Client(connection, ManagerDialectRules.DescribeTimeout).GetJsonAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is MediaManagerHttpException or MediaManagerUnreachableException)
        {
            return new ManagerDescription(
                connection,
                SignalStatus.Unreachable,
                capabilities,
                [],
                [],
                UnreachableText(connection, exception, _profile.IsArr ? "which folders it manages" : "what it manages"),
                new SortedSet<string>(StringComparer.Ordinal),
                MayAnswerLater: MayAnswerLater(exception));
        }

        if (!_profile.IsArr)
        {
            return ManagerDialectRules.ExternalDescription(connection, capabilities, payload);
        }

        var (roots, libraries) = ManagerDialectRules.ArrRootFolders(payload, _profile.ArrScope!);
        return new ManagerDescription(connection, SignalStatus.Reported, capabilities, roots, libraries, AdvertisedCapabilities: new SortedSet<string>(StringComparer.Ordinal));
    }

    public async Task<ManagerQueueSignal> QueueRowsAsync(ManagerConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        WireValue? payload;
        try
        {
            var client = Client(connection, ManagerDialectRules.QueueTimeout);
            payload = _profile.IsArr
                ? await client.GetJsonAsync("/api/v3/queue", [new("pageSize", ManagerDialectRules.ArrQueuePageSize)], cancellationToken).ConfigureAwait(false)
                : await client.GetJsonAsync(ManagerDialectRules.ExternalQueuePath, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is MediaManagerHttpException or MediaManagerUnreachableException)
        {
            return new ManagerQueueSignal(connection, SignalStatus.Unreachable, [], UnreachableText(connection, exception, "what it is importing"));
        }

        if (_profile.IsArr)
        {
            return new ManagerQueueSignal(connection, SignalStatus.Reported, ManagerDialectRules.ArrQueueRows(payload, _profile.ArrScope!));
        }

        var rows = ManagerDialectRules.ExternalQueueEntries(payload)
            .Select(ManagerDialectRules.ExternalQueueRow)
            .OfType<ManagerQueueRow>()
            .ToList();
        return new ManagerQueueSignal(connection, SignalStatus.Reported, rows);
    }

    public async Task RemoveQueueItemAsync(ManagerConnection connection, WireObject row, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(row);
        if (!_profile.IsArr)
        {
            throw new MediaManagerHttpException(
                $"{connection.Label} takes a rejection through its hand-off report, not by removing a queue item.");
        }

        var queueId = ManagerValues.FirstNumber(row, "id")
            ?? throw new MediaManagerHttpException($"{connection.Label}'s queue item has no id.");
        await Client(connection, ManagerDialectRules.QueueTimeout).DeleteAsync(
            $"/api/v3/queue/{queueId.ToString(CultureInfo.InvariantCulture)}",
            [new("removeFromClient", true), new("blocklist", true)],
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ManagerLibraryTruth> LibraryTruthAsync(ManagerConnection connection, string mediaScope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!_profile.IsArr)
        {
            return new ManagerLibraryTruth(
                connection,
                SignalStatus.NoSignal,
                [],
                $"{connection.Label} tells Weir what it manages and what it is importing, but not which " +
                "individual files it still keeps, so it cannot clear a folder for deletion.");
        }

        if (mediaScope != _profile.ArrScope)
        {
            return new ManagerLibraryTruth(connection, SignalStatus.NoSignal, [], $"{connection.Label} does not look after this kind of library.");
        }

        var client = Client(connection, ManagerDialectRules.LibraryTimeout);
        try
        {
            if (mediaScope == MediaManagerKinds.Tv)
            {
                var paths = (await SeriesEpisodeFilesAsync(client, cancellationToken).ConfigureAwait(false))
                    .SelectMany(series => ManagerDialectRules.ArrLibraryFilePaths(series.Files, _profile.ArrFileKey))
                    .ToList();
                return new ManagerLibraryTruth(connection, SignalStatus.Reported, paths);
            }

            var payload = await client
                .GetJsonAsync(_profile.ArrLibraryPath!, [new("pageSize", ManagerDialectRules.ArrLibraryPageSize)], cancellationToken)
                .ConfigureAwait(false);
            return new ManagerLibraryTruth(connection, SignalStatus.Reported, ManagerDialectRules.ArrLibraryFilePaths(payload, _profile.ArrFileKey));
        }
        catch (Exception exception) when (exception is MediaManagerHttpException or MediaManagerUnreachableException)
        {
            return new ManagerLibraryTruth(connection, SignalStatus.Unreachable, [], UnreachableText(connection, exception, "which files it still keeps"));
        }
    }

    public async Task<ManagerLibraryFilesSignal> ListLibraryFilesAsync(ManagerConnection connection, string mediaScope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!_profile.IsArr)
        {
            return new ManagerLibraryFilesSignal(
                connection,
                SignalStatus.NoSignal,
                [],
                $"{connection.Label} does not offer a file-by-file library listing Weir can match to a title.");
        }

        if (mediaScope != _profile.ArrScope)
        {
            return new ManagerLibraryFilesSignal(connection, SignalStatus.NoSignal, [], $"{connection.Label} does not look after this kind of library.");
        }

        var client = Client(connection, ManagerDialectRules.LibraryTimeout);
        try
        {
            if (mediaScope == MediaManagerKinds.Movie)
            {
                var payload = await client.GetJsonAsync("/api/v3/movie", [new("pageSize", ManagerDialectRules.ArrLibraryPageSize)], cancellationToken).ConfigureAwait(false);
                return new ManagerLibraryFilesSignal(connection, SignalStatus.Reported, ManagerDialectRules.ArrMovieLibraryFiles(payload));
            }

            var files = new List<ManagerLibraryFile>();
            foreach (var series in await SeriesEpisodeFilesAsync(client, cancellationToken).ConfigureAwait(false))
            {
                files.AddRange(ManagerDialectRules.ArrEpisodeLibraryFiles(series.Files, series.Id, series.Title, series.QualityProfileId));
            }

            return new ManagerLibraryFilesSignal(connection, SignalStatus.Reported, files);
        }
        catch (Exception exception) when (exception is MediaManagerHttpException or MediaManagerUnreachableException)
        {
            return new ManagerLibraryFilesSignal(connection, SignalStatus.Unreachable, [], UnreachableText(connection, exception, "which files it keeps and their titles"));
        }
    }

    /// <summary>One Sonarr series with the answer to "which files does it have".</summary>
    private sealed record SeriesFiles(string Id, string Title, long? QualityProfileId, WireValue? Files);

    /// <summary>
    /// Every series with its episode files. Sonarr has no "every episode file" endpoint: <c>GET /api/v3/episodefile</c> answers 400 unless it is
    /// given a <c>seriesId</c> or <c>episodeFileIds</c> (recorded from Sonarr 4.0.20), so every series is listed first and asked for its files in turn.
    /// </summary>
    private static async Task<List<SeriesFiles>> SeriesEpisodeFilesAsync(MediaManagerHttpClient client, CancellationToken cancellationToken)
    {
        var seriesPayload = await client.GetJsonAsync("/api/v3/series", [new("pageSize", ManagerDialectRules.ArrLibraryPageSize)], cancellationToken).ConfigureAwait(false);
        var all = new List<SeriesFiles>();
        foreach (var series in ManagerValues.Dicts(seriesPayload))
        {
            if (ManagerValues.FirstNumber(series, "id") is not { } seriesId)
            {
                continue;
            }

            var idText = seriesId.ToString(CultureInfo.InvariantCulture);
            var title = ManagerValues.FirstText(series, "title") ?? idText;
            var qualityProfileId = ManagerValues.FirstNumber(series, "qualityProfileId") is { } profileNumber ? (long)profileNumber : (long?)null;
            var episodePayload = await client.GetJsonAsync("/api/v3/episodefile", [new("seriesId", seriesId)], cancellationToken).ConfigureAwait(false);
            all.Add(new SeriesFiles(idText, title, qualityProfileId, episodePayload));
        }

        return all;
    }

    public async Task<ManagerNotifyOutcome> FileChangedAsync(
        ManagerConnection connection,
        IReadOnlySet<string> advertisedCapabilities,
        string? titleId,
        string filePath,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(advertisedCapabilities);
        ArgumentNullException.ThrowIfNull(filePath);
        var client = Client(connection, ManagerDialectRules.QueueTimeout);
        if (_profile.IsArr)
        {
            if (titleId is null || !long.TryParse(titleId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                throw new MediaManagerHttpException($"{connection.Label} needs a matched title id to rescan {filePath}, and none was given.");
            }

            var (name, idProperty) = ManagerDialectRules.ArrRescanCommand(_profile.ArrScope!);
            await client.PostJsonAsync("/api/v3/command", new WireObject().Set("name", name).Set(idProperty, id), cancellationToken: cancellationToken).ConfigureAwait(false);
            return ManagerNotifyOutcome.Notified;
        }

        if (!advertisedCapabilities.Contains(ManagerDialectRules.ExternalFileChangedCapability))
        {
            return ManagerNotifyOutcome.NotSupported;
        }

        var body = new WireObject().Set("path", filePath).Set("tool", "Weir");
        if (!string.IsNullOrEmpty(reason))
        {
            body.Set("reason", reason);
        }

        // Deluno answers 202 Accepted (a queued re-read), not one of the three statuses every other call here accepts.
        await client.PostJsonAsync(ManagerDialectRules.ExternalFileChangedPath, body, acceptedStatuses: [200, 201, 202, 204], cancellationToken: cancellationToken).ConfigureAwait(false);
        return ManagerNotifyOutcome.Notified;
    }

    private MediaManagerHttpClient Client(ManagerConnection connection, TimeSpan timeout) =>
        new(connection.BaseUrl, connection.ApiKey, _handlers, timeout, connection.Reference);
}
