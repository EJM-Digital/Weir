using Weir.Core.Artwork;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Artwork;

/// <summary>
/// Gives a file its title, so it shows that title's poster. The title is read from the file's name, or taken from what the
/// media manager that handed the file over said about it, and queued for a poster lookup once however many files share it.
/// </summary>
public sealed class ArtworkSubjects
{
    private const string FilmScope = "movie";
    private const string SeriesScope = "tv";

    private readonly ArtworkLookupStore _lookups;
    private readonly ArtworkFileStore _files;

    public ArtworkSubjects(ArtworkLookupStore lookups, ArtworkFileStore files)
    {
        _lookups = lookups ?? throw new ArgumentNullException(nameof(lookups));
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    /// <summary>Read a file's title from its name and queue it. A file that already has a title keeps it.</summary>
    public async Task LinkFromNameAsync(UnitOfWork uow, ArtworkCandidate file, int priority)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(file);
        var scope = ScopeOf(file.MediaScope);
        var title = ArtworkTitleReader.Read(scope, file.Path, file.KnownTitle);
        var key = title is null ? null : await QueueTitleAsync(uow, scope, title, priority).ConfigureAwait(false);
        await _files.LinkAsync(uow, file.LibraryId, file.Path, key).ConfigureAwait(false);
    }

    /// <summary>
    /// Record what a media manager said about the title of each file it handed over, which replaces what was read from the
    /// names. Without any hints a file is read from its name as usual.
    /// </summary>
    public async Task LinkHandoffAsync(UnitOfWork uow, long libraryId, string mediaScope, IEnumerable<string> paths, string? releaseName, ArtworkHints? hints)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(paths);
        var scope = ScopeOf(mediaScope);
        foreach (var path in paths)
        {
            if (hints is null)
            {
                await LinkFromNameAsync(uow, new ArtworkCandidate(libraryId, path, scope, releaseName), ArtworkPriority.Processing).ConfigureAwait(false);
                continue;
            }

            var title = hints.Title is { } named
                ? new ArtworkTitle(named, hints.Year)
                : ArtworkTitleReader.Read(scope, path, releaseName) is { } read ? read with { Year = hints.Year ?? read.Year } : null;
            var key = await QueueAsync(uow, scope, title, hints, ArtworkPriority.Processing).ConfigureAwait(false);
            await _files.RelinkAsync(uow, libraryId, path, key, hints.Season, hints.Episode).ConfigureAwait(false);
        }
    }

    /// <summary>Queue a title read from a file's name for a caller that needs its answer now, and return the key it is queued under.</summary>
    public async Task<string> QueueTitleAsync(UnitOfWork uow, string mediaScope, ArtworkTitle title, int priority)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(title);
        var scope = ScopeOf(mediaScope);
        var key = ArtworkKeys.ForTitle(scope, title);
        await _lookups.EnqueueAsync(uow, new ArtworkLookupRequest(key, scope, title.Title, title.Year, TmdbId: null, TvdbId: null, ImdbId: null, PosterRef: null, priority)).ConfigureAwait(false);
        return key;
    }

    /// <summary>The lookup key the title is queued under, or null when there is nothing to look a poster up by.</summary>
    private async Task<string?> QueueAsync(UnitOfWork uow, string scope, ArtworkTitle? title, ArtworkHints hints, int priority)
    {
        var key = hints.TmdbId is { } tmdbId ? ArtworkKeys.ForTmdbId(scope, tmdbId)
            : title is not null ? ArtworkKeys.ForTitle(scope, title)
            : hints.PosterRef is { } posterFile ? ArtworkKeys.ForPosterRef(posterFile)
            : null;
        if (key is null)
        {
            return null;
        }

        await _lookups.EnqueueAsync(uow, new ArtworkLookupRequest(
            key, scope, title?.Title ?? string.Empty, title?.Year, hints.TmdbId, hints.TvdbId, hints.ImdbId, hints.PosterRef, priority)).ConfigureAwait(false);
        return key;
    }

    /// <summary>A library's kind as lookups know it: a series, or a film for anything else.</summary>
    public static string ScopeOf(string libraryMediaType) => libraryMediaType == SeriesScope ? SeriesScope : FilmScope;
}
