using Weir.Core.Security;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Notifications;
using Weir.Infrastructure.Settings;

namespace Weir.Infrastructure.Tests;

/// <summary>The stores a configuration bundle and a backup are built from, sharing one credentials secret.</summary>
internal static class BackupParts
{
    public const string CredentialsSecret = "backup-parts-credentials-secret";

    public static CredentialCipher Cipher() => new(CredentialsSecret, null, [], TimeProvider.System);

    public static BackupPasswordStore Passwords(DataChangePublisher? changes = null) => new(Cipher(), changes);

    public static ConfigurationBundleConnections Connections(
        NotificationChannelStore? channels = null, MediaManagerConnectionStore? managers = null, DownloadClientConnectionStore? clients = null) =>
        new(channels ?? new NotificationChannelStore(), managers ?? new MediaManagerConnectionStore(), clients ?? new DownloadClientConnectionStore(), Cipher());
}
