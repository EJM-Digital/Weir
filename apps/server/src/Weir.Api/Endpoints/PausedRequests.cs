using Microsoft.AspNetCore.Http;
using Weir.Api.Http;
using Weir.Core.Time;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Endpoints;

/// <summary>
/// While processing is paused nothing changes a media file, so a request a person makes by hand that would is refused with the
/// reason, before anything on disk or at a media manager is touched. The same request goes through once processing is resumed.
/// </summary>
internal static class PausedRequests
{
    public const string DeleteDownload = "Processing is paused, so Weir did not delete this download. Resume processing and try again.";

    public const string DownloadAgain = "Processing is paused, so Weir did not delete this file or ask for a new copy. Resume processing and try again.";

    public static async Task RefuseWhilePausedAsync(SuitePauseService pause, ApiRequest request, UnitOfWork uow, string message)
    {
        ArgumentNullException.ThrowIfNull(pause);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(uow);
        if ((await pause.CurrentAsync(uow, Timestamp.UtcNow(request.Time)).ConfigureAwait(false)).Paused)
        {
            throw new ApiException(StatusCodes.Status409Conflict, message);
        }
    }
}
