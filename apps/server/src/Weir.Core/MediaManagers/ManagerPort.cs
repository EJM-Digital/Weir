using Weir.Core.Json;

namespace Weir.Core.MediaManagers;

/// <summary>The kinds and scopes a media manager connection can have.</summary>
public static class MediaManagerKinds
{
    /// <summary>The connection-less generic source: a manager with no dialect of its own.</summary>
    public const string Native = "native";

    /// <summary>Every manager kind.</summary>
    public static readonly IReadOnlyList<string> All = ["radarr", "sonarr", "deluno", Native];

    /// <summary>The lane names an older client still puts in the retired lane route. Searching is the manager's own.</summary>
    public static readonly IReadOnlyList<string> RetiredSearchLanes = ["missing", "upgrade"];

    public const string Movie = "movie";
    public const string Tv = "tv";

    /// <summary>Every media scope.</summary>
    public static readonly IReadOnlySet<string> AllMediaScopes = new SortedSet<string>(StringComparer.Ordinal) { Movie, Tv };

    private static readonly Dictionary<string, string> KindLabels = new(StringComparer.Ordinal)
    {
        ["radarr"] = "Radarr",
        ["sonarr"] = "Sonarr",
        ["deluno"] = "Deluno",
        ["native"] = "Media manager",
    };

    /// <summary>The product a kind is, as people know it: "Radarr", "Deluno"; "Media manager" for one Weir has no name for.</summary>
    public static string ProductLabel(string? kind) =>
        KindLabels.GetValueOrDefault(WireStrings.Strip(kind ?? string.Empty).ToLowerInvariant(), "Media manager");

    /// <summary>A connection's label; see <see cref="ConnectionLabels.For"/>.</summary>
    public static string LabelForConnection(string? kind, string? name, string? nickname = null) => ConnectionLabels.For(ProductLabel(kind), name, nickname);
}

/// <summary>How a connection is written in a sentence, shared by media managers and download clients.</summary>
public static class ConnectionLabels
{
    /// <summary>
    /// The connection's name, led by its product unless the name already starts with it: "Deluno on RIG" stays
    /// as it is, and a name of "Main" reads "Deluno (Main)". No name reads as the product alone. A nickname follows
    /// the rest: "Radarr on nas · 4K".
    /// </summary>
    public static string For(string product, string? name, string? nickname = null)
    {
        var label = WireStrings.Strip(name ?? string.Empty);
        if (label.Length == 0)
        {
            label = product;
        }
        else if (!label.StartsWith(product, StringComparison.OrdinalIgnoreCase))
        {
            label = $"{product} ({label})";
        }

        return ConnectionNicknames.Normalize(nickname) is { } shown ? label + ConnectionNicknames.Separator + shown : label;
    }
}

/// <summary>The three answers a manager can give.</summary>
public static class SignalStatus
{
    public const string Reported = "reported";
    public const string NoSignal = "no_signal";
    public const string Unreachable = "unreachable";
}

/// <summary>One configured manager, resolved far enough to talk to. <see cref="ConnectionId"/> is null for the environment credentials.</summary>
public sealed record ManagerConnection(string Kind, string Name, string BaseUrl, string ApiKey, long? ConnectionId = null, string? Nickname = null)
{
    public string Label => MediaManagerKinds.LabelForConnection(Kind, Name, Nickname);

    /// <summary>The saved connection this is, or null for the environment credentials.</summary>
    public ConnectionRef? Reference => ConnectionRef.ForManager(ConnectionId);
}

/// <summary>One in-progress item, tagged with the scope whose dialect can read it.</summary>
public sealed record ManagerQueueRow(string Scope, WireObject Payload);

/// <summary>What one manager said when asked whether it is mid-import. Only reported-and-empty means clear.</summary>
public sealed record ManagerQueueSignal(ManagerConnection Connection, string Status, IReadOnlyList<ManagerQueueRow> Rows, string? Detail = null)
{
    public bool IsReported => Status == SignalStatus.Reported;
}

/// <summary>Library files one manager still keeps. Only reported-and-empty clears a delete.</summary>
public sealed record ManagerLibraryTruth(ManagerConnection Connection, string Status, IReadOnlyList<string> LibraryFilePaths, string? Detail = null)
{
    public bool IsReported => Status == SignalStatus.Reported;
}

/// <summary>
/// One file a manager's library holds, matched to the title that owns it (<see cref="IMediaManagerPort.ListLibraryFilesAsync"/>, #507).
/// <see cref="FileId"/> and <see cref="QualityProfileId"/> (#551) are read from the same movie/series payload
/// this file was already found in — a movie's own <c>movieFile.id</c>, an episode file row's own <c>id</c>,
/// and either resource's own <c>qualityProfileId</c> — so resolving them costs no extra round trip. Null for
/// a manager kind or shape that does not carry them (there is none among Sonarr/Radarr today, but the fields
/// stay optional rather than assumed always present).
/// </summary>
public sealed record ManagerLibraryFile(string TitleId, string TitleName, string FilePath, long? FileId = null, long? QualityProfileId = null);

/// <summary>Every library file one manager reports for a scope, matched to titles, or why it could not (#507).</summary>
public sealed record ManagerLibraryFilesSignal(ManagerConnection Connection, string Status, IReadOnlyList<ManagerLibraryFile> Files, string? Detail = null)
{
    public bool IsReported => Status == SignalStatus.Reported;
}

/// <summary>What happened when a manager was asked to re-read a changed file (<see cref="IMediaManagerPort.FileChangedAsync"/>, #507).</summary>
public enum ManagerNotifyOutcome
{
    /// <summary>The manager accepted the call.</summary>
    Notified,

    /// <summary>This manager does not offer the signal at all (Deluno without the manifest capability) — no call was made.</summary>
    NotSupported,
}

/// <summary>What a kind of manager can be asked, before anyone talks to it.</summary>
public sealed record ManagerCapabilities(IReadOnlySet<string> Scopes, bool ReportsQueue, bool ReportsLibraryTruth, string Summary, bool RemovesQueueItems = false);

/// <summary>
/// One library a manager says it looks after. <see cref="RootPath"/> is a path on the manager's host.
/// <see cref="DownloadsPath"/> is where the manager says this library's downloads arrive (Deluno's manifest
/// <c>downloadsPath</c>), which is where its hand-offs come from; null when the manager does not say.
/// </summary>
public sealed record ManagerLibraryDescriptor(
    string Key,
    string Name,
    string? MediaScope,
    string? RootPath = null,
    string? OutputPath = null,
    bool ProcessesBeforeImport = false,
    string? DownloadsPath = null);

/// <summary>A live answer to "what do you manage", degrading to the static capabilities.</summary>
public sealed record ManagerDescription(
    ManagerConnection Connection,
    string Status,
    ManagerCapabilities Capabilities,
    IReadOnlyList<string> LibraryRoots,
    IReadOnlyList<ManagerLibraryDescriptor> Libraries,
    string? Detail = null,
    IReadOnlySet<string>? AdvertisedCapabilities = null,
    IReadOnlyList<ManagerDownloadClientDescriptor>? DownloadClients = null,
    bool MayAnswerLater = false);

/// <summary>A call to a manager failed.</summary>
public class MediaManagerHttpException : Exception
{
    public MediaManagerHttpException()
    {
    }

    public MediaManagerHttpException(string message)
        : base(message)
    {
    }

    public MediaManagerHttpException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MediaManagerHttpException(string message, int statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public MediaManagerHttpException(string message, int statusCode, string? errorCode)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    /// <summary>The HTTP status the manager answered with, when it answered at all.</summary>
    public int? StatusCode { get; }

    /// <summary>
    /// The machine code a manager named in its answer's <c>error</c> member (Deluno's <c>library_not_found</c>), when the answer
    /// carried one in the form of a code: lower-case words joined by underscores. Nothing else of the answer is kept.
    /// </summary>
    public string? ErrorCode { get; }
}

/// <summary>The manager answered 429. Back off; never retry inside the call.</summary>
public sealed class MediaManagerRateLimitedException : MediaManagerHttpException
{
    public MediaManagerRateLimitedException()
    {
    }

    public MediaManagerRateLimitedException(string message)
        : base(message)
    {
    }

    public MediaManagerRateLimitedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MediaManagerRateLimitedException(string message, double? retryAfterSeconds)
        : base(message)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }

    public double? RetryAfterSeconds { get; }
}

/// <summary>A manager answered with a redirect, which Weir refuses to follow (real Sonarr, Radarr and Deluno never send one).</summary>
public sealed class MediaManagerRedirectedException : MediaManagerHttpException
{
    public MediaManagerRedirectedException()
        : base("answered with a redirect to another address. Use the address it redirects to.")
    {
    }
}

/// <summary>The manager could not be reached at all (refused, timed out, name not resolved).</summary>
public sealed class MediaManagerUnreachableException : Exception
{
    public MediaManagerUnreachableException()
    {
    }

    public MediaManagerUnreachableException(string message)
        : base(message)
    {
    }

    public MediaManagerUnreachableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>One kind of manager answering the three questions.</summary>
public interface IMediaManagerPort
{
    string Kind { get; }

    /// <summary>What this kind can be asked. No network.</summary>
    ManagerCapabilities Capabilities();

    Task<ManagerDescription> DescribeAsync(ManagerConnection connection, CancellationToken cancellationToken = default);

    Task<ManagerQueueSignal> QueueRowsAsync(ManagerConnection connection, CancellationToken cancellationToken = default);

    /// <summary>Remove one queue item and blocklist its release. Throws <see cref="MediaManagerHttpException"/> when refused.</summary>
    Task RemoveQueueItemAsync(ManagerConnection connection, WireObject row, CancellationToken cancellationToken = default);

    Task<ManagerLibraryTruth> LibraryTruthAsync(ManagerConnection connection, string mediaScope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every library file this manager knows for <paramref name="mediaScope"/>, matched to its title
    /// (#507). <see cref="SignalStatus.NoSignal"/> for a manager with no
    /// per-file listing (Deluno's external API offers none); no network is spent finding that out.
    /// </summary>
    Task<ManagerLibraryFilesSignal> ListLibraryFilesAsync(ManagerConnection connection, string mediaScope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tell this manager one of its files changed on disk, so it re-reads it (#507).
    /// <paramref name="advertisedCapabilities"/> is the manifest capability set from a prior
    /// <see cref="DescribeAsync"/> (avoids a second round trip just to check it); <paramref name="titleId"/>
    /// is required for an arr manager (the id <see cref="ListLibraryFilesAsync"/> matched) and ignored by an
    /// external one, which matches by <paramref name="filePath"/> itself. Throws
    /// <see cref="MediaManagerHttpException"/> or <see cref="MediaManagerUnreachableException"/> when the
    /// call was attempted and failed; returns <see cref="ManagerNotifyOutcome.NotSupported"/> without any
    /// network call when this manager cannot be asked at all.
    /// </summary>
    Task<ManagerNotifyOutcome> FileChangedAsync(
        ManagerConnection connection,
        IReadOnlySet<string> advertisedCapabilities,
        string? titleId,
        string filePath,
        string? reason,
        CancellationToken cancellationToken = default);
}
