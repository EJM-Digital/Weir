using System.Globalization;
using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// <see cref="IManagerRedownload"/> for Sonarr/Radarr (#509).
/// <para><c>DELETE /api/v3/moviefile/{id}</c> or <c>episodefile/{id}</c> removes the file from disk, into the
/// manager's recycle bin when one is configured and permanently otherwise. <b>This is the destructive step.</b>
/// The file record must be deleted before searching, because Radarr and Sonarr reject a same-quality release as
/// "not an upgrade" unless its custom-format score is higher, so a search alone would download nothing.</para>
/// <para>The search is <c>MoviesSearchCommand</c> <c>{movieIds}</c> or <c>SeriesSearchCommand</c> <c>{seriesId}</c>,
/// posted to <c>/api/v3/command</c>. TV works at series granularity because Sonarr file listings map to a series id
/// (see <see cref="HttpMediaManagerPort.ListLibraryFilesAsync"/>); <c>EpisodeSearchCommand</c> would need an extra
/// episode lookup.</para>
/// </summary>
public sealed class ArrManagerRedownload : IManagerRedownload
{
    private readonly IManagerHttpHandlerFactory _handlers;

    public ArrManagerRedownload(IManagerHttpHandlerFactory handlers)
    {
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
    }

    public bool SupportsRedownload(string? kind) => ManagerRedownloadRules.KindSupportsRedownload(kind);

    public async Task<RedownloadResult> RequestRedownloadAsync(
        ManagerConnection connection,
        string mediaScope,
        string titleId,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(mediaScope);
        ArgumentNullException.ThrowIfNull(filePath);
        var profile = ManagerKindProfiles.ForKind(connection.Kind);
        if (profile is not { IsArr: true } || profile.ArrScope != mediaScope)
        {
            return Unsupported(connection);
        }

        if (!long.TryParse(titleId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            throw new MediaManagerHttpException($"{connection.Label} needs a matched title id to redownload {filePath}, and none was given.");
        }

        var client = new MediaManagerHttpClient(connection.BaseUrl, connection.ApiKey, _handlers, ManagerDialectRules.LibraryTimeout, connection.Reference);
        return mediaScope == MediaManagerKinds.Movie
            ? await RequestMovieRedownloadAsync(connection, client, id, cancellationToken).ConfigureAwait(false)
            : await RequestSeriesRedownloadAsync(connection, client, id, filePath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RedownloadResult> RequestMovieRedownloadAsync(
        ManagerConnection connection, MediaManagerHttpClient client, long movieId, CancellationToken cancellationToken)
    {
        var idText = movieId.ToString(CultureInfo.InvariantCulture);
        var payload = await client.GetJsonAsync($"/api/v3/movie/{idText}", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (payload is not WireObject movie)
        {
            throw new MediaManagerHttpException($"{connection.Label} did not return a movie for id {idText}.");
        }

        var file = movie.Get("movieFile") as WireObject;
        var fileId = file is null ? null : ManagerValues.FirstNumber(file, "id");
        var sizeBytes = file is null ? null : (long?)ManagerValues.FirstNumber(file, "size");

        return await DeleteThenSearchAsync(
            connection,
            client,
            existingFileId: fileId is { } n ? (long)n : null,
            existingFileSizeBytes: sizeBytes,
            deletePath: existingId => $"/api/v3/moviefile/{existingId.ToString(CultureInfo.InvariantCulture)}",
            searchBody: new WireObject().Set("name", "MoviesSearch").Set("movieIds", new WireArray([WireValue.Of(movieId)])),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RedownloadResult> RequestSeriesRedownloadAsync(
        ManagerConnection connection, MediaManagerHttpClient client, long seriesId, string filePath, CancellationToken cancellationToken)
    {
        var payload = await client.GetJsonAsync(
            "/api/v3/episodefile",
            [new("seriesId", seriesId)],
            cancellationToken).ConfigureAwait(false);
        var match = ManagerValues.Dicts(payload).FirstOrDefault(row => LibraryFileChangeRules.PathsEqual(ManagerValues.Text(row.Get("path")), filePath));
        var fileId = match is null ? null : ManagerValues.FirstNumber(match, "id");
        var sizeBytes = match is null ? null : (long?)ManagerValues.FirstNumber(match, "size");

        return await DeleteThenSearchAsync(
            connection,
            client,
            existingFileId: fileId is { } n ? (long)n : null,
            existingFileSizeBytes: sizeBytes,
            deletePath: existingId => $"/api/v3/episodefile/{existingId.ToString(CultureInfo.InvariantCulture)}",
            searchBody: new WireObject().Set("name", "SeriesSearch").Set("seriesId", seriesId),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The shared shape: delete the existing file when there is one (the destructive step), then dispatch a
    /// search. A search failure is only ever swallowed into <see cref="RedownloadOutcome.DeletedButSearchFailed"/>
    /// when the delete already happened — otherwise it propagates, since nothing destructive occurred yet.
    /// </summary>
    private static async Task<RedownloadResult> DeleteThenSearchAsync(
        ManagerConnection connection,
        MediaManagerHttpClient client,
        long? existingFileId,
        long? existingFileSizeBytes,
        Func<long, string> deletePath,
        WireObject searchBody,
        CancellationToken cancellationToken)
    {
        var deleted = false;
        if (existingFileId is { } id)
        {
            await client.DeleteAsync(deletePath(id), cancellationToken: cancellationToken).ConfigureAwait(false);
            deleted = true;
        }

        try
        {
            await client.PostJsonAsync("/api/v3/command", searchBody, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (deleted && exception is MediaManagerHttpException or MediaManagerUnreachableException)
        {
            return new RedownloadResult(
                connection,
                RedownloadOutcome.DeletedButSearchFailed,
                DeletedExistingFile: true,
                existingFileSizeBytes,
                $"{connection.Label} deleted the existing file, but Weir could not ask it to search for a replacement. " +
                "This title now has no file until one is searched for again: try Download " +
                $"again, or search for it manually in {connection.Label}.",
                exception.Message);
        }

        return new RedownloadResult(
            connection,
            RedownloadOutcome.Requested,
            DeletedExistingFile: deleted,
            existingFileSizeBytes,
            deleted
                ? $"{connection.Label} deleted the existing file and is searching for a replacement."
                : $"{connection.Label} is searching for this title; it had no existing file for Weir to delete first.");
    }

    private static RedownloadResult Unsupported(ManagerConnection connection) => new(
        connection,
        RedownloadOutcome.Unsupported,
        DeletedExistingFile: false,
        ExistingFileSizeBytes: null,
        $"{connection.Label} cannot be asked to download this title again.");
}
