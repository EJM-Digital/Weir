using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>How long a start that works on a large database asks the tray to wait in silence (see the measurements on <see cref="StartupPatience"/>).</summary>
public sealed class StartupPatienceTests : IDisposable
{
    private const long Gigabyte = 1024L * 1024 * 1024;
    private readonly TempDirectory _home = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void An_empty_database_asks_for_the_base_allowance_only()
    {
        Assert.Equal(StartupPatience.Base, StartupPatience.ForBytes(0));
        Assert.Equal(StartupPatience.Base, StartupPatience.ForBytes(-5));
    }

    [Fact]
    public void Every_gigabyte_asks_for_the_same_time_more()
    {
        Assert.Equal(StartupPatience.Base + StartupPatience.PerGigabyte, StartupPatience.ForBytes(Gigabyte));
        Assert.Equal(StartupPatience.Base + (StartupPatience.PerGigabyte * 3), StartupPatience.ForBytes(3 * Gigabyte));
    }

    [Fact]
    public void The_allowance_is_more_than_twice_the_slowest_start_measured_for_the_size()
    {
        // 201 s to save the copy of 1.5 GB, the slowest seen, with a disk and a CPU both busy.
        var slowestMeasured = TimeSpan.FromSeconds(201);

        Assert.True(StartupPatience.ForBytes((long)(1.5 * Gigabyte)) > slowestMeasured * 2);
    }

    [Fact]
    public void The_size_is_that_of_the_database_and_its_write_ahead_log()
    {
        var database = _home.Join("weir.sqlite3");
        File.WriteAllBytes(database, new byte[1_000]);
        File.WriteAllBytes(database + "-wal", new byte[2_000]);

        Assert.Equal(StartupPatience.ForBytes(3_000), StartupPatience.ForDatabase(database));
    }

    [Fact]
    public void A_database_that_is_not_there_yet_asks_for_the_base_allowance()
    {
        Assert.Equal(StartupPatience.Base, StartupPatience.ForDatabase(_home.Join("not-yet.sqlite3")));
    }
}
