using Microsoft.Data.Sqlite;
using Weir.Core.Notifications;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Http;

public sealed partial class NotificationDispatcher
{
    private const string SignInRecoveredEvent = "auth_sign_in_recovered";

    /// <summary>
    /// Tells every alert that is switched on that the sign-in was recovered with a recovery code, without the caller waiting. It goes to
    /// them all, whichever job events they listen for: a person should not have to choose to hear about this. Never throws; a failure
    /// is logged through <paramref name="warn"/>.
    /// </summary>
    public void DispatchSecurityAlert(SqliteDatabase database, string title, string detail, Action<string, Exception?> warn)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(warn);
        Track(Task.Run(async () =>
        {
            List<NotificationChannelRecord> channels;
            try
            {
                var uow = await UnitOfWork.OpenAsync(database).ConfigureAwait(false);
                await using (uow.ConfigureAwait(false))
                {
                    channels = [.. (await _channels.ListAsync(uow).ConfigureAwait(false)).Where(channel => channel.Enabled)];
                }
            }
            catch (Exception exception) when (exception is SqliteException or IOException or InvalidOperationException)
            {
                warn($"Notification dispatch: failed to read channels for event={SignInRecoveredEvent}", exception);
                return;
            }

            await DeliverAsync(channels, title, detail, SignInRecoveredEvent, "auth", 0, "auth.sign_in_recovered", warn).ConfigureAwait(false);
        }));
    }
}
