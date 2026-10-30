using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Settings;

/// <summary>
/// Asking for the settings row when there is none (a first request and a periodic task at the same moment) makes it once, and neither
/// asker fails. Seen in a full browser run: the backup task's tick failed with "UNIQUE constraint failed: suite_settings.id".
/// </summary>
public sealed class SuiteSettingsEnsureTests : IDisposable
{
    private readonly StoreFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Many_askers_at_once_for_a_missing_row_all_get_it_and_it_is_made_once()
    {
        var store = new SuiteSettingsStore(new AuthStore());
        for (var round = 0; round < 25; round++)
        {
            await _fixture.Execute("DELETE FROM suite_settings");

            var askers = Enumerable.Range(0, 8).Select(_ => Task.Run(() => _fixture.WithUnitOfWork(uow => store.EnsureAsync(uow))));
            var rows = await Task.WhenAll(askers);

            Assert.All(rows, row => Assert.Equal("UTC", row.AppTimezone));
            Assert.Equal(1, await _fixture.Scalar("SELECT count(*) FROM suite_settings"));
        }
    }
}
