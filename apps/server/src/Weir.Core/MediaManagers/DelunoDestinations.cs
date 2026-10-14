using Weir.Core.Json;

namespace Weir.Core.MediaManagers;

/// <summary>
/// One of Deluno's processor path mappings: <see cref="DelunoPath"/> is a folder as Deluno's machine sees it and
/// <see cref="ProcessorPath"/> is the same folder as Weir sees it.
/// </summary>
public sealed record DelunoPathMapping(string DelunoPath, string ProcessorPath);

/// <summary>
/// Where one download client saves the finished downloads Deluno sends it for a library. <see cref="Status"/> is
/// <see cref="Ok"/>, <see cref="Problem"/> or <see cref="Unknown"/>, and <see cref="Message"/> is Deluno's own plain
/// sentence for it. <see cref="SaveFolder"/> is in Deluno's view.
/// </summary>
public sealed record DelunoDestination(
    string ClientName,
    string Category,
    string CategoryKind,
    string? SaveFolder,
    string SavedBy,
    string Status,
    string Message)
{
    public const string Ok = "ok";
    public const string Problem = "problem";
    public const string Unknown = "unknown";

    /// <summary>The client's own folder for the category, not one Deluno chooses for each grab.</summary>
    public const string SavedByClientCategory = "client-category";
}

/// <summary>
/// One library as <c>GET /api/integrations/processors/download-destinations</c> describes it. Every path is in Deluno's
/// view; <see cref="PathMappings"/> (enabled only, highest priority first) turns them into Weir's.
/// </summary>
public sealed record DelunoLibraryDestinations(
    string LibraryId,
    string LibraryName,
    string? DownloadsPath,
    string? ProcessorOutputPath,
    IReadOnlyList<DelunoDestination> Destinations,
    IReadOnlyList<DelunoPathMapping> PathMappings);

/// <summary>What asking Deluno where a library's downloads land came to.</summary>
public enum DelunoDestinationsStatus
{
    /// <summary>Deluno answered with the destinations.</summary>
    Read,

    /// <summary>The route does not exist: a Deluno older than the release that added it.</summary>
    NotOffered,

    /// <summary>Deluno has the route but answered <c>library_not_found</c>: it no longer has the library asked about.</summary>
    LibraryNotFound,

    /// <summary>Deluno refused the key because it lacks the Imports scope.</summary>
    NeedsImportsScope,

    /// <summary>Deluno could not be asked, or answered something Weir cannot use; <see cref="DelunoDestinationsAnswer.Detail"/> says so in plain words.</summary>
    Unreachable,
}

public sealed record DelunoDestinationsAnswer(
    DelunoDestinationsStatus Status,
    IReadOnlyList<DelunoLibraryDestinations> Libraries,
    string? Detail = null)
{
    public static DelunoDestinationsAnswer Read(IReadOnlyList<DelunoLibraryDestinations> libraries) => new(DelunoDestinationsStatus.Read, libraries);

    public static DelunoDestinationsAnswer Failed(DelunoDestinationsStatus status, string? detail = null) => new(status, [], detail);
}

/// <summary>Reads Deluno's download-destinations answer into <see cref="DelunoLibraryDestinations"/>; fields Weir does not use are ignored.</summary>
public static class DelunoDestinationRules
{
    /// <summary>The <c>error</c> code of Deluno's 404 for a library id it does not have (Deluno #1267); a 404 without it is a missing route.</summary>
    public const string LibraryNotFoundCode = "library_not_found";

    public const string DownloadDestinationsPath = "/api/integrations/processors/download-destinations";

    /// <summary>The first Deluno release that serves <see cref="DownloadDestinationsPath"/>, as named to people.</summary>
    public const string FirstVersionOffering = "1.0.0-rc.23";

    public static List<DelunoLibraryDestinations> ParseLibraries(WireValue? payload) =>
        payload is WireObject answer
            ? [.. ManagerValues.Dicts(answer.Get("libraries")).Select(ParseLibrary).OfType<DelunoLibraryDestinations>()]
            : [];

    private static DelunoLibraryDestinations? ParseLibrary(WireObject library)
    {
        var id = ManagerDialectRules.ManifestLibraryKey(library, "libraryId");
        if (id is null)
        {
            return null;
        }

        return new DelunoLibraryDestinations(
            id,
            ManagerValues.FirstText(library, "libraryName") ?? id,
            ManagerValues.FirstText(library, "downloadsPath"),
            ManagerValues.FirstText(library, "processorOutputPath"),
            [.. ManagerValues.Dicts(library.Get("destinations")).Select(ParseDestination)],
            library.Get("processorConnection") is WireObject connection ? ParseMappings(connection) : []);
    }

    private static DelunoDestination ParseDestination(WireObject destination) => new(
        ManagerValues.FirstText(destination, "downloadClientName") ?? "its download client",
        ManagerValues.FirstText(destination, "category") ?? string.Empty,
        ManagerValues.FirstText(destination, "categoryKind") ?? "category",
        ManagerValues.FirstText(destination, "saveFolder"),
        ManagerValues.FirstText(destination, "savedBy") ?? string.Empty,
        ManagerValues.FirstText(destination, "status")?.ToLowerInvariant() ?? DelunoDestination.Unknown,
        ManagerValues.FirstText(destination, "message") ?? string.Empty);

    private static List<DelunoPathMapping> ParseMappings(WireObject connection) =>
        [.. ManagerValues.Dicts(connection.Get("pathMappings"))
            .Select(mapping => (Deluno: ManagerValues.FirstText(mapping, "delunoPath"), Processor: ManagerValues.FirstText(mapping, "processorPath")))
            .Where(pair => pair.Deluno is not null && pair.Processor is not null)
            .Select(pair => new DelunoPathMapping(pair.Deluno!, pair.Processor!))];
}
