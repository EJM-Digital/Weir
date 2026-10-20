using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>Placing an output under the manager's own path space, for a library that refines before import.</summary>
public sealed partial class HandoffCompletionReporter
{
    /// <summary>
    /// How many times the manager is asked for its libraries when placing an output, and how long the first wait is (it doubles for
    /// each further one). A manager that dropped one request answers the second or third time. One that is down stays unreadable, and
    /// the report waits for it (<see cref="Unplaced"/>).
    /// </summary>
    private const int LibraryReadAttempts = 3;

    private static readonly TimeSpan LibraryReadWait = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// A report whose output paths Weir could not yet write as the manager sees them, because the manager would not list its libraries.
    /// <paramref name="LocalFolder"/> is the output folder the paths are under, and <paramref name="State"/> is the ledger state the
    /// hand-off takes once they are placed.
    /// </summary>
    private sealed record Unplaced(string LocalFolder, string State);

    /// <summary>The manager's output folder for a hand-off's library: the folder, none when that library does not refine before import, or unreadable.</summary>
    private readonly record struct ManagerOutputFolder(string? Path, bool Readable);

    /// <summary>
    /// <paramref name="outputFile"/> rebuilt under the manager's output folder, or null when it is
    /// not under ours.
    /// </summary>
    public static string? TranslateOutputPath(string outputFile, string localOutputFolder, string managerOutputFolder)
    {
        string file;
        string folder;
        try
        {
            file = Path.GetFullPath(outputFile);
            folder = Path.GetFullPath(localOutputFolder);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var fileParts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Where(p => p.Length > 0).ToList();
        var folderParts = folder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Where(p => p.Length > 0).ToList();
        if (fileParts.Count <= folderParts.Count || !folderParts.Select((part, i) => string.Equals(part, fileParts[i], comparison)).All(same => same))
        {
            return null;
        }

        return CompletionReports.ManagerPathJoin(managerOutputFolder, fileParts.Skip(folderParts.Count));
    }

    /// <summary>The output as the manager will see it, or null to fall back to the local path.</summary>
    private async Task<string?> ManagerOutputPathAsync(ManagerConnection connection, HandoffOrigin origin, WireObject result, CancellationToken cancellationToken) =>
        (await PlaceOutputAsync(connection, origin, result, cancellationToken).ConfigureAwait(false)).ManagerPath;

    /// <summary>
    /// The output as the manager will see it. <c>ManagerPath</c> is null when the report carries the local path (the library does not
    /// refine before import, or the output is not under the library's output folder). <c>LocalFolder</c> is set instead when the manager
    /// would not list its libraries: the path cannot be placed, and the report must wait rather than name a folder the manager cannot open.
    /// </summary>
    private async Task<(string? ManagerPath, string? LocalFolder)> PlaceOutputAsync(
        ManagerConnection connection, HandoffOrigin origin, WireObject result, CancellationToken cancellationToken)
    {
        if (result.Get("output_file") is not WireString outputFile || result.Get("processing_output_folder_resolved") is not WireString localFolder)
        {
            return (null, null);
        }

        var folder = await ManagerOutputFolderAsync(connection, origin, cancellationToken).ConfigureAwait(false);
        if (!folder.Readable)
        {
            return (null, localFolder.Value);
        }

        return (folder.Path is { } managerFolder ? TranslateOutputPath(outputFile.Value, localFolder.Value, managerFolder) : null, null);
    }

    /// <summary>
    /// The output folder the manager's own library uses for this hand-off, as the manager sees it, when that library
    /// processes before import. <c>Path</c> is null when there is none; <c>Readable</c> is false when the manager would not say.
    /// </summary>
    private async Task<ManagerOutputFolder> ManagerOutputFolderAsync(ManagerConnection connection, HandoffOrigin origin, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(origin.LibraryId) || _connections.Ports.PortForKind(connection.Kind) is not { } port)
        {
            return new ManagerOutputFolder(null, true);
        }

        for (var attempt = 1; ; attempt++)
        {
            ManagerDescription description;
            try
            {
                description = await port.DescribeAsync(connection, cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // An unreadable library list only means the output path cannot be placed; the report still goes out.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                _logger.LogWarning(exception, "Could not read {Label}'s libraries to place the output path.", connection.Label);
                return new ManagerOutputFolder(null, true);
            }

            if (description.Status != SignalStatus.Unreachable || !description.MayAnswerLater)
            {
                return new ManagerOutputFolder(
                    description.Libraries
                        .FirstOrDefault(library => library.Key == origin.LibraryId && library.ProcessesBeforeImport && !string.IsNullOrEmpty(library.OutputPath))
                        ?.OutputPath,
                    true);
            }

            if (attempt == LibraryReadAttempts)
            {
                LogLibrariesUnreadable(_logger, connection.Label, attempt);
                return new ManagerOutputFolder(null, false);
            }

            await Task.Delay(LibraryReadWait * Math.Pow(2, attempt - 1), _time, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Label} would not list its libraries after {Attempts} tries, so Weir cannot say where it sees the output; the report waits for it.")]
    private static partial void LogLibrariesUnreadable(ILogger logger, string label, int attempts);
}
