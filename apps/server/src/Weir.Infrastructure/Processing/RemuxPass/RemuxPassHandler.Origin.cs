using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing.RemuxPass;

public sealed partial class RemuxPassHandler
{
    internal async Task<WireObject?> CarriedOriginAsync(long jobId, long? libraryId, string rel, string mediaScope, CancellationToken cancellationToken)
    {
        WireObject? carried;
        try
        {
            var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
            await using (uow.ConfigureAwait(false))
            {
                var library = await ResolveLibraryAsync(uow, _libraries, libraryId, mediaScope).ConfigureAwait(false);
                carried = await HandoffOriginCarry.FindAsync(uow, library?.Id ?? libraryId, rel, jobId).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Weir could not look up the hand-off this file came from.");
            carried = null;
        }

        return carried;
    }

    private static bool IsSameHandoff(WireObject? current, WireObject adopted) =>
        current is not null &&
        HandoffOrigin.FromPayload(new WireObject().Set("origin", current)) is { } before &&
        HandoffOrigin.FromPayload(new WireObject().Set("origin", adopted)) is { } after &&
        before.SourceKey == after.SourceKey && before.HandoffId == after.HandoffId;

    /// <summary>The origin written onto this job's own row after it started, or null.</summary>
    internal async Task<WireObject?> AdoptedOriginAsync(long jobId, CancellationToken cancellationToken)
    {
        WireObject? adopted;
        try
        {
            var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
            await using (uow.ConfigureAwait(false))
            {
                var payload = await uow.ScalarAsync("SELECT payload_json FROM jobs WHERE id = @id", ("@id", jobId)).ConfigureAwait(false);
                adopted = payload is string text && WireJsonParser.Parse(text) is WireObject dict ? dict.Get("origin") as WireObject : null;
            }
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or WireJsonDecodeException)
        {
            _logger.LogWarning(exception, "Weir could not check whether a hand-off took over this pass.");
            adopted = null;
        }

        return adopted;
    }
}
