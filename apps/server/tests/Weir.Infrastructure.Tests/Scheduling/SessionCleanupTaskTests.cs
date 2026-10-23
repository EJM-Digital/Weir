using Microsoft.Extensions.Logging.Abstractions;
using Weir.Infrastructure.Scheduling;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Scheduling;

/// <summary>The hourly clean-up of sign-ins that can never be used again.</summary>
public sealed class SessionCleanupTaskTests : IDisposable
{
    private readonly StoreFixture _store = new();

    public void Dispose() => _store.Dispose();

    private async Task<long> SessionCountAsync()
    {
        using var connection = _store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM user_sessions";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task A_cleanup_with_no_sign_ins_to_remove_leaves_things_as_they_are()
    {
        var task = new SessionCleanupTask(_store.Database, _store.Auth, NullLoggerFactory.Instance);

        await task.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, await SessionCountAsync());
    }
}
