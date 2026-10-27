using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing;

/// <summary>
/// Create Processing libraries from what a connected media manager already knows (#554).
///
/// The operator typing a watched folder into Weir is re-entering information the manager holds and keeps
/// current. Deluno publishes a manifest built for exactly this, and the arr products expose a cruder
/// version of the same thing through their root folders.
///
/// Two rules shape everything here, and both come from what Processing does after a successful pass — it
/// deletes source folders:
///
/// <b>Re-sync reports, it never applies.</b> A watched folder that silently repoints is a destructive
/// surprise. Drift is surfaced with the manager's value and Weir's own value side by side, and the
/// operator decides. The one exception is a manager that reports its folders (Deluno): the watched and output
/// folders of a workflow linked to it belong to it and <see cref="ManagerWorkflowSync"/> keeps them in step.
///
/// <b>A path on the manager's host is not automatically a path Weir can see.</b> The check is purely
/// textual, the same approach <see cref="Weir.Core.MediaManagers.HandoffPaths"/> already uses, so it
/// behaves identically on the API host and the worker and fails with a sentence rather than a stat error
/// on an unmounted share.
/// </summary>
public sealed class LibraryDiscoveryService
{
    private readonly MediaManagerConnectionService _connections;
    private readonly IMediaManagerPorts _ports;
    private readonly LibraryStore _libraries;

    public LibraryDiscoveryService(MediaManagerConnectionService connections, IMediaManagerPorts ports, LibraryStore libraries)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
    }

    /// <summary>
    /// The folder a discovered library should watch. For a manager that hands files over before importing (Deluno's
    /// Refine before import), that is where its downloads arrive (<see cref="ManagerLibraryDescriptor.DownloadsPath"/>),
    /// because a hand-off's file must sit inside the watched folder (<see cref="Weir.Core.MediaManagers.HandoffPaths"/>);
    /// the library root is where finished media ends up and never where a hand-off comes from. Otherwise, and for a
    /// manager that does not report a downloads folder, the library root as before.
    /// </summary>
    public static string? WatchedSource(ManagerLibraryDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.ProcessesBeforeImport && !string.IsNullOrWhiteSpace(descriptor.DownloadsPath)
            ? descriptor.DownloadsPath
            : descriptor.RootPath;
    }

    /// <summary>
    /// Why a manager-reported root cannot be used locally, or <see langword="null"/>
    /// if it can. Deliberately not a filesystem check first: a share that is simply not mounted yet should
    /// read as "not visible here", not as a crash. The existence check comes last and only sharpens the message.
    /// </summary>
    public static string? LocalPathProblem(string? rootPath)
    {
        var raw = WireStrings.Strip(rootPath ?? string.Empty);
        if (raw.Length == 0)
        {
            return "The manager did not say where this library lives, so Weir has no folder to watch. " +
                   "Set the watched folder yourself after importing.";
        }

        if (!LibraryDiscoveryRules.LooksAbsolute(raw))
        {
            return $"The manager reported {WireStrings.Repr(raw)}, which is not an absolute path Weir can resolve.";
        }

        if (!Directory.Exists(raw))
        {
            return $"The manager sees this library at {WireStrings.Repr(raw)}. That path does not exist on the machine " +
                   "running Weir — both hosts have to see the same folder at the same path. Mount it there, or set " +
                   "Weir's own watched folder after importing.";
        }

        return null;
    }

    /// <summary>The libraries the manager describes; throws with an operator-facing sentence when it cannot be asked or does not answer.</summary>
    private async Task<IReadOnlyList<ManagerLibraryDescriptor>> DescriptorsForAsync(
        UnitOfWork uow, MediaManagerConnectionRecord connectionRow, CancellationToken cancellationToken)
    {
        var resolved = await _connections.ConnectionsByIdAsync(uow, [connectionRow.Id]).ConfigureAwait(false);
        if (resolved.Count == 0)
        {
            throw new ProcessingDiscoveryException(
                $"{connectionRow.Name} has no saved address and API key, so Weir cannot ask it anything.");
        }

        var port = _ports.PortForKind(connectionRow.Kind);
        if (port is null)
        {
            throw new ProcessingDiscoveryException($"Weir does not know how to talk to a {connectionRow.Kind} connection.");
        }

        var described = await port.DescribeAsync(resolved[0], cancellationToken).ConfigureAwait(false);
        if (described.Status != SignalStatus.Reported)
        {
            throw new ProcessingDiscoveryException(described.Detail ?? $"{resolved[0].Label} did not answer when asked what it manages.");
        }

        return described.Libraries;
    }

    /// <summary>What this manager reports, marked up with what Weir already has.</summary>
    public async Task<List<DiscoverableLibrary>> DiscoverableLibrariesAsync(
        UnitOfWork uow, MediaManagerConnectionRecord connectionRow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(connectionRow);

        var imported = new HashSet<(long? ConnectionId, string Key)>();
        foreach (var row in await _libraries.ListAsync(uow).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(row.DiscoveredLibraryKey))
            {
                imported.Add((row.DiscoveredFromConnectionId, row.DiscoveredLibraryKey));
            }
        }

        var descriptors = await DescriptorsForAsync(uow, connectionRow, cancellationToken).ConfigureAwait(false);
        var result = new List<DiscoverableLibrary>();
        foreach (var descriptor in descriptors)
        {
            result.Add(new DiscoverableLibrary(
                Key: descriptor.Key,
                Name: descriptor.Name,
                MediaType: descriptor.MediaScope,
                RootPath: WatchedSource(descriptor),
                AlreadyImported: imported.Contains((connectionRow.Id, descriptor.Key)),
                LocalPathProblem: LocalPathProblem(WatchedSource(descriptor)),
                OutputPath: descriptor.OutputPath,
                ProcessesBeforeImport: descriptor.ProcessesBeforeImport,
                OutputPathProblem: descriptor.ProcessesBeforeImport ? LocalPathProblem(descriptor.OutputPath) : null));
        }

        return result;
    }

    /// <summary><paramref name="wanted"/>, or the first numbered variant of it not already in <paramref name="existing"/>.</summary>
    internal static string UniqueName(HashSet<string> existing, string wanted)
    {
        if (!existing.Contains(wanted))
        {
            return wanted;
        }

        for (var suffix = 2; suffix < 100; suffix++)
        {
            var candidate = $"{wanted} ({suffix})";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new ProcessingDiscoveryException($"Too many workflows already named like {WireStrings.Repr(wanted)}.");
    }

    /// <summary>
    /// Creates a Processing library per selected manager library.
    ///
    /// The manager's id is stored as a durable integration reference, which is what Deluno's own guidance
    /// asks external tools to keep. Everything else is an ordinary library: editable afterwards, and
    /// indistinguishable from a hand-made one everywhere else.
    /// </summary>
    public async Task<List<ProcessingLibraryRecord>> ImportLibrariesAsync(
        UnitOfWork uow, MediaManagerConnectionRecord connectionRow, IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(connectionRow);
        ArgumentNullException.ThrowIfNull(keys);

        var wanted = keys.Where(key => !string.IsNullOrEmpty(key)).ToList();
        if (wanted.Count == 0)
        {
            throw new ProcessingDiscoveryException("Choose at least one library to import.");
        }

        var descriptors = await DescriptorsForAsync(uow, connectionRow, cancellationToken).ConfigureAwait(false);
        var byKey = new Dictionary<string, ManagerLibraryDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            byKey[descriptor.Key] = descriptor;
        }

        var missing = wanted.Where(key => !byKey.ContainsKey(key)).ToList();
        if (missing.Count > 0)
        {
            throw new ProcessingDiscoveryException(
                $"{connectionRow.Name} no longer reports a library with id {missing[0]}. Refresh the list and try again.");
        }

        var existingLibraries = await _libraries.ListAsync(uow).ConfigureAwait(false);
        var order = existingLibraries.Select(row => row.DisplayOrder).DefaultIfEmpty(0).Max();
        var existingNames = existingLibraries.Select(row => row.Name).ToHashSet(StringComparer.Ordinal);

        var created = new List<ProcessingLibraryRecord>();
        foreach (var key in wanted)
        {
            var descriptor = byKey[key];
            order += 1;

            // An unusable root is imported as an empty watched folder rather than a path Weir cannot see: a
            // library pointed at a folder that is not there would fail every scan, and the operator is told
            // why on the way in.
            var usableRoot = LocalPathProblem(WatchedSource(descriptor)) is null ? WatchedSource(descriptor)! : string.Empty;

            // A manager that processes before importing tells Weir where it expects the finished file.
            // Without this a discovered library arrived with no output folder and could not run a pass at
            // all, which made discovery a half-import (#364). Held to the same textual local-path check as
            // the watched folder, for the same reason: the manager's path is not necessarily one Weir can see.
            var usableOutput = descriptor.ProcessesBeforeImport && LocalPathProblem(descriptor.OutputPath) is null
                ? descriptor.OutputPath!
                : string.Empty;

            var name = UniqueName(existingNames, descriptor.Name);
            existingNames.Add(name);

            // A library with nowhere to hand files back arrives switched off: on, it would pick up
            // downloads it could never deliver. Setup › Workflows says what it needs.
            var row = new ProcessingLibraryRecord
            {
                Name = name,
                Enabled = usableOutput.Length > 0,
                MediaType = string.IsNullOrEmpty(descriptor.MediaScope) ? ProcessingMediaScopes.Movie : descriptor.MediaScope,
                DisplayOrder = order,
                WatchedFolder = usableRoot,
                OutputFolder = usableOutput,
                DiscoveredFromConnectionId = connectionRow.Id,
                DiscoveredLibraryKey = key,
            };

            created.Add(await _libraries.CreateDiscoveredAsync(uow, row).ConfigureAwait(false));
        }

        return created;
    }

    /// <summary>Differences between the manager and Weir. Reported only, never applied.</summary>
    public async Task<List<LibraryDrift>> ResyncDriftAsync(
        UnitOfWork uow, MediaManagerConnectionRecord connectionRow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(connectionRow);

        var descriptors = await DescriptorsForAsync(uow, connectionRow, cancellationToken).ConfigureAwait(false);
        var descriptorsByKey = new Dictionary<string, ManagerLibraryDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            descriptorsByKey[descriptor.Key] = descriptor;
        }

        var linked = (await _libraries.ListAsync(uow).ConfigureAwait(false))
            .Where(row => row.DiscoveredFromConnectionId == connectionRow.Id && !string.IsNullOrEmpty(row.DiscoveredLibraryKey))
            .ToList();

        var drift = new List<LibraryDrift>();
        foreach (var row in linked)
        {
            if (!descriptorsByKey.TryGetValue(row.DiscoveredLibraryKey!, out var descriptor))
            {
                drift.Add(new LibraryDrift(
                    LibraryDriftKinds.LibraryRemoved,
                    row.Id,
                    row.Name,
                    null,
                    string.IsNullOrEmpty(row.WatchedFolder) ? null : row.WatchedFolder,
                    $"{connectionRow.Name} no longer reports the library this workflow came from. Weir has left it exactly as it is — " +
                    "remove it here if it is genuinely gone, or unlink it to keep it as a Weir-only workflow."));
                continue;
            }

            var managerRoot = WireStrings.Strip(WatchedSource(descriptor) ?? string.Empty);
            var saved = WireStrings.Strip(row.WatchedFolder);
            if (managerRoot.Length > 0 && saved.Length > 0 &&
                LibraryDiscoveryRules.Comparable(managerRoot) != LibraryDiscoveryRules.Comparable(saved))
            {
                drift.Add(new LibraryDrift(
                    LibraryDriftKinds.RootMoved,
                    row.Id,
                    row.Name,
                    managerRoot,
                    saved,
                    $"{connectionRow.Name} now puts this workflow's library at {WireStrings.Repr(managerRoot)}, but Weir is " +
                    $"watching {WireStrings.Repr(saved)}. Nothing has been changed — Weir deletes source folders after a " +
                    "successful pass, so a watched folder only moves when you move it."));
            }

            var problem = LocalPathProblem(WatchedSource(descriptor));
            if (problem is not null && saved.Length == 0)
            {
                drift.Add(new LibraryDrift(
                    LibraryDriftKinds.PathNotLocal,
                    row.Id,
                    row.Name,
                    managerRoot.Length > 0 ? managerRoot : null,
                    null,
                    problem));
            }
        }

        var known = linked.Select(row => row.DiscoveredLibraryKey).ToHashSet(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            if (known.Contains(descriptor.Key))
            {
                continue;
            }

            drift.Add(new LibraryDrift(
                LibraryDriftKinds.LibraryAdded,
                null,
                descriptor.Name,
                WatchedSource(descriptor),
                null,
                $"{connectionRow.Name} reports a library Weir has not imported. Import it if you want Weir to process it."));
        }

        return drift;
    }

    /// <summary>Forgets where a library came from, keeping the library itself untouched.</summary>
    public Task<ProcessingLibraryRecord> UnlinkLibraryAsync(UnitOfWork uow, ProcessingLibraryRecord row) =>
        _libraries.UnlinkAsync(uow, row);
}
