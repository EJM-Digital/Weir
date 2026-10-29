using Microsoft.Data.Sqlite;
using Weir.Core.Net;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Settings;

/// <summary>
/// Skip sign-in for devices on the home network: the one row that holds whether it is on, the owner's own networks and the
/// allow list. No row means off, as it was before the setting existed.
/// </summary>
public sealed class HomeNetworkSignInStore
{
    private const char Separator = '\n';

    private readonly DataChangePublisher? _changes;

    /// <summary>Once a save commits, <paramref name="changes"/> tells every open Security page to read it again.</summary>
    public HomeNetworkSignInStore(DataChangePublisher? changes = null)
    {
        _changes = changes;
    }

    public async Task<HomeNetworkSettings> GetAsync(UnitOfWork uow)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return await uow.QuerySingleAsync("SELECT enabled, extra_networks, allow_list FROM home_network_sign_in WHERE id = 1", Read).ConfigureAwait(false)
            ?? HomeNetworkSettings.Off;
    }

    public async Task SaveAsync(UnitOfWork uow, HomeNetworkSettings settings)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(settings);
        await uow.ExecuteAsync(
            "INSERT INTO home_network_sign_in (id, enabled, extra_networks, allow_list) VALUES (1, $enabled, $extra, $allow) " +
            "ON CONFLICT (id) DO UPDATE SET enabled = excluded.enabled, extra_networks = excluded.extra_networks, " +
            "allow_list = excluded.allow_list, updated_at = CURRENT_TIMESTAMP",
            ("$enabled", settings.Enabled ? 1 : 0),
            ("$extra", string.Join(Separator, settings.ExtraNetworks)),
            ("$allow", string.Join(Separator, settings.AllowList))).ConfigureAwait(false);
        _changes?.PublishOnCommit(uow, DataTopics.Settings);
    }

    private static HomeNetworkSettings Read(SqliteDataReader reader) =>
        new(SqliteValues.GetBool(reader, 0), Lines(SqliteValues.GetString(reader, 1)), Lines(SqliteValues.GetString(reader, 2)));

    private static string[] Lines(string text) => text.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
}
