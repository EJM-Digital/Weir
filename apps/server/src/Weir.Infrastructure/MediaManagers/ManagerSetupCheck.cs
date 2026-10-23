using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// "Will this media manager pick up what this library writes?" for every enabled Sonarr, Radarr and Deluno connection that
/// covers a media type (<see cref="ManagerSetupRules"/> holds the rules and their Sonarr/Radarr/Deluno citations).
/// </summary>
/// <remarks>
/// Read only, by construction: the only calls are <c>GET</c>s — <see cref="ManagerSetupRules.RemotePathMappingPath"/>,
/// <see cref="ManagerSetupRules.DownloadClientPath"/>, <see cref="ManagerSetupRules.DownloadClientConfigPath"/> and the
/// queue (<see cref="IMediaManagerPort.QueueRowsAsync"/>) for Sonarr/Radarr, and for Deluno its manifest through
/// <see cref="IMediaManagerPort.DescribeAsync"/> and its download destinations (<see cref="DelunoDestinationRules.DownloadDestinationsPath"/>). Weir never changes a
/// manager's settings; the user makes the mapping, and this says whether it is right.
/// </remarks>
public sealed partial class ManagerSetupCheck
{
    private readonly MediaManagerConnectionService _connections;
    private readonly MediaManagerConnectionStore _connectionStore;
    private readonly IManagerHttpHandlerFactory _handlers;
    private readonly IFolderProbe _folders;
    private readonly ILogger<ManagerSetupCheck> _logger;

    public ManagerSetupCheck(
        MediaManagerConnectionService connections,
        MediaManagerConnectionStore connectionStore,
        IManagerHttpHandlerFactory handlers,
        IFolderProbe folders,
        ILogger<ManagerSetupCheck> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(connectionStore);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(logger);
        _connections = connections;
        _connectionStore = connectionStore;
        _handlers = handlers;
        _folders = folders;
        _logger = logger;
    }

    /// <summary>The plain sentence for a manager that did not answer a check; the technical reason goes to the log only.</summary>
    private string UnreachableText(ManagerConnection connection, Exception exception, string what)
    {
        LogManagerDidNotAnswer(_logger, exception, connection.Label, what);
        return ManagerDialectRules.Unreachable(connection, exception, what);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Label} did not answer when Weir checked {What}.")]
    private static partial void LogManagerDidNotAnswer(ILogger logger, Exception error, string label, string what);

    /// <summary>
    /// One entry per enabled connection that covers <paramref name="mediaScope"/>, in connection order. A workflow only
    /// depends on the managers it is linked to, so <paramref name="linkedConnectionIds"/> narrows the answer to those; null
    /// means every connection that covers the media type, as for folders not yet saved. <paramref name="delunoLibrary"/> is
    /// the Deluno library the workflow was created from, when it was: that connection's check is made against that library.
    /// Each Deluno connection is asked once per call.
    /// </summary>
    public async Task<List<WireObject>> CheckAsync(
        UnitOfWork uow,
        string mediaScope,
        string watchedFolder,
        string outputFolder,
        IReadOnlySet<long>? linkedConnectionIds,
        bool removesOriginals = true,
        DelunoLibraryLink? delunoLibrary = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var results = new List<WireObject>();
        foreach (var row in await _connectionStore.ListEnabledAsync(uow).ConfigureAwait(false))
        {
            var isArr = IsArrFor(row, mediaScope);
            if ((!isArr && !IsDeluno(row)) || (linkedConnectionIds is not null && !linkedConnectionIds.Contains(row.Id)))
            {
                continue;
            }

            var label = row.Label;
            var entry = new WireObject()
                .Set("connection_id", row.Id)
                .Set("kind", row.Kind)
                .Set("name", row.Name)
                .Set("label", label)
                .Set("flow", isArr ? "remote_path_mapping" : "handoff");
            IReadOnlyList<SetupCheckLine> lines;
            var connection = _connections.ConnectionFromRow(row);
            if (connection is null)
            {
                lines = [new SetupCheckLine(SetupCheckLine.Problem, MissingCredentialsText(label))];
            }
            else if (isArr)
            {
                (var hosts, lines, var suggestedWatchedFolder, var arrFacts) = await CheckArrAsync(connection, mediaScope, watchedFolder, outputFolder, removesOriginals, cancellationToken).ConfigureAwait(false);
                entry.Set("story", SourceFactsOut(arrFacts));
                entry.Set("mapping", new WireObject()
                    .Set("hosts", new WireArray(hosts.Select(host => (WireValue)new WireString(host))))
                    .Set("remote_path", WireStrings.Strip(watchedFolder))
                    .Set("local_path", WireStrings.Strip(outputFolder)));
                entry.Set("suggested_watched_folder", suggestedWatchedFolder);
            }
            else
            {
                var link = delunoLibrary is { } linked && linked.ConnectionId == row.Id ? linked : null;
                var (deluno, delunoFacts) = await CheckDelunoAsync(connection, label, mediaScope, watchedFolder, outputFolder, link, cancellationToken).ConfigureAwait(false);
                entry.Set("story", SourceFactsOut(delunoFacts));
                lines = deluno.Lines;
                entry.Set("suggested_watched_folder", deluno.WatchedFolder).Set("suggested_output_folder", deluno.OutputFolder);
            }

            entry.Set("ready", lines.All(line => line.State != SetupCheckLine.Problem));
            entry.Set("lines", new WireArray(lines.Select(line => (WireValue)line.ToOut())));
            results.Add(entry);
        }

        return results;
    }

    /// <summary>
    /// What each enabled connection that covers <paramref name="mediaScope"/> reports as the folder its downloads land in,
    /// with no library to compare against: Sonarr's and Radarr's own download client directory, Deluno's Refine-before-import
    /// downloads and output folders. A connection that cannot say still gets an entry, with the reason.
    /// </summary>
    public async Task<List<ManagerFolderSuggestion>> SuggestFoldersAsync(UnitOfWork uow, string mediaScope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var results = new List<ManagerFolderSuggestion>();
        foreach (var row in await _connectionStore.ListEnabledAsync(uow).ConfigureAwait(false))
        {
            var isArr = IsArrFor(row, mediaScope);
            if (!isArr && !IsDeluno(row))
            {
                continue;
            }

            if (_connections.ConnectionFromRow(row) is not { } connection)
            {
                var label = row.Label;
                results.Add(new ManagerFolderSuggestion(row.Id, label, null, null, MissingCredentialsText(label)));
                continue;
            }

            results.Add(isArr
                ? await SuggestArrFoldersAsync(row.Id, connection, mediaScope, cancellationToken).ConfigureAwait(false)
                : await SuggestDelunoFoldersAsync(row.Id, connection, mediaScope, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private static bool IsArrFor(MediaManagerConnectionRecord row, string mediaScope) =>
        ManagerKindProfiles.ForKind(row.Kind) is { IsArr: true } profile && profile.ArrScope == mediaScope;

    private static bool IsDeluno(MediaManagerConnectionRecord row) => string.Equals(row.Kind, "deluno", StringComparison.OrdinalIgnoreCase);

    private static string MissingCredentialsText(string label) =>
        $"{label} has no address or API key saved, so Weir cannot check it. Add them under Setup › Connections › Media managers.";

    private async Task<ManagerFolderSuggestion> SuggestArrFoldersAsync(
        long connectionId, ManagerConnection connection, string mediaScope, CancellationToken cancellationToken)
    {
        WireValue? clients;
        try
        {
            var client = new MediaManagerHttpClient(connection.BaseUrl, connection.ApiKey, _handlers, ManagerDialectRules.DescribeTimeout, connection.Reference);
            clients = await client.GetJsonAsync(ManagerSetupRules.DownloadClientPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is MediaManagerHttpException or MediaManagerUnreachableException)
        {
            return new ManagerFolderSuggestion(
                connectionId, connection.Label, null, null, UnreachableText(connection, exception, "where its downloads are saved"));
        }

        var folder = WatchFolderSuggestionRules.SuggestArrWatchedFolder(ManagerSetupRules.ParseDownloadClients(clients, mediaScope));
        return new ManagerFolderSuggestion(
            connectionId, connection.Label, folder, null, folder is null ? $"{connection.Label} does not say where its downloads are saved." : null);
    }

    private async Task<ManagerFolderSuggestion> SuggestDelunoFoldersAsync(
        long connectionId, ManagerConnection connection, string mediaScope, CancellationToken cancellationToken)
    {
        var deluno = await ReadDelunoFoldersAsync(connection, mediaScope, cancellationToken).ConfigureAwait(false);
        var problem = deluno.WatchedFolder is null ? deluno.Lines.Select(line => line.Text).FirstOrDefault() : null;
        return new ManagerFolderSuggestion(connectionId, connection.Label, deluno.WatchedFolder, deluno.OutputFolder, problem);
    }

    private static WireObject SourceFactsOut(ManagerSourceFacts facts) => new WireObject()
        .Set("source_category", facts.Category)
        .Set("manager_library", facts.ManagerLibrary)
        .Set("root_folder", facts.RootFolder);

    private async Task<(IReadOnlyList<string> Hosts, IReadOnlyList<SetupCheckLine> Lines, string? SuggestedWatchedFolder, ManagerSourceFacts Facts)> CheckArrAsync(
        ManagerConnection connection, string mediaScope, string watchedFolder, string outputFolder, bool removesOriginals, CancellationToken cancellationToken)
    {
        WireValue? mappings;
        WireValue? clients;
        try
        {
            var client = new MediaManagerHttpClient(connection.BaseUrl, connection.ApiKey, _handlers, ManagerDialectRules.DescribeTimeout, connection.Reference);
            mappings = await client.GetJsonAsync(ManagerSetupRules.RemotePathMappingPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            clients = await client.GetJsonAsync(ManagerSetupRules.DownloadClientPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is MediaManagerHttpException or MediaManagerUnreachableException)
        {
            return ([], [new SetupCheckLine(SetupCheckLine.Problem, UnreachableText(connection, exception, "its remote path mappings"))], null, ManagerSourceFacts.None);
        }

        var parsedClients = ManagerSetupRules.ParseDownloadClients(clients, mediaScope);
        var result = ManagerSetupRules.EvaluateArr(
            connection.Label,
            mediaScope,
            watchedFolder,
            outputFolder,
            ManagerSetupRules.ParseMappings(mappings),
            parsedClients,
            await CompletedDownloadHandlingAsync(connection, cancellationToken).ConfigureAwait(false),
            await QueueOutputPathsAsync(connection, cancellationToken).ConfigureAwait(false),
            removesOriginals);
        var facts = ManagerSourceFactsRules.ForArr(parsedClients, await RootFoldersAsync(connection, cancellationToken).ConfigureAwait(false));
        return (result.Hosts, result.Lines, WatchFolderSuggestionRules.SuggestArrWatchedFolder(parsedClients), facts);
    }

    /// <summary>The root folders the manager imports into, through its port; none when it cannot say.</summary>
    private async Task<IReadOnlyList<string>> RootFoldersAsync(ManagerConnection connection, CancellationToken cancellationToken)
    {
        // Only Sonarr, Radarr and Deluno connections are checked, and every kind Weir knows has a port.
        var port = _connections.Ports.PortForKind(connection.Kind)!;
        var description = await port.DescribeAsync(connection, cancellationToken).ConfigureAwait(false);
        return description.Status == SignalStatus.Reported ? description.LibraryRoots : [];
    }

    /// <summary>Queued downloads' <c>outputPath</c>s, through the manager port's own queue read; none when the queue cannot be read.</summary>
    private async Task<IReadOnlyList<string>> QueueOutputPathsAsync(ManagerConnection connection, CancellationToken cancellationToken)
    {
        var port = _connections.Ports.PortForKind(connection.Kind)!;
        var signal = await port.QueueRowsAsync(connection, cancellationToken).ConfigureAwait(false);
        return signal.IsReported
            ? [.. signal.Rows.Select(row => ManagerValues.FirstText(row.Payload, "outputPath")).OfType<string>()]
            : [];
    }

    /// <summary><c>enableCompletedDownloadHandling</c>, or null when it cannot be read: a missing answer is not a problem.</summary>
    private async Task<bool?> CompletedDownloadHandlingAsync(ManagerConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var client = new MediaManagerHttpClient(connection.BaseUrl, connection.ApiKey, _handlers, ManagerDialectRules.DescribeTimeout, connection.Reference);
            return await client.GetJsonAsync(ManagerSetupRules.DownloadClientConfigPath, cancellationToken: cancellationToken).ConfigureAwait(false) is WireObject config &&
                   config.Get("enableCompletedDownloadHandling") is WireBool flag
                ? flag.Value
                : null;
        }
        catch (Exception exception) when (exception is MediaManagerHttpException or MediaManagerUnreachableException)
        {
            return null;
        }
    }

}
