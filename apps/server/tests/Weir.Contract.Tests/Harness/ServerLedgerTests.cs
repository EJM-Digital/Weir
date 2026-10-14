using System.Diagnostics;
using System.Text.Json;

namespace Weir.Contract.Tests.Harness;

/// <summary>
/// The ledger is one folder shared by every test fixture in a run, and by every run on the machine, so its callers meet.
/// Fixtures start their servers at the same moment (each looks for orphans first) and stop them at the same moment (each
/// forgets its own), and none of that may throw or stop one orphan twice.
/// </summary>
[ContractArea("harness")]
public sealed class ServerLedgerTests : IDisposable
{
    private const int Callers = 16;

    private readonly string _folder = Directory.CreateTempSubdirectory("weir_ledger_").FullName;
    private readonly List<Process> _started = [];

    public void Dispose()
    {
        foreach (var process in _started)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
        }

        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task Callers_that_clear_the_same_stale_entries_at_once_never_throw()
    {
        for (var entry = 0; entry < 40; entry++)
        {
            Plant(serverProcessId: 2_000_000 + entry, serverStartTicks: 1, ownerProcessId: 2_100_000 + entry, ownerStartTicks: 1);
        }

        var results = await AtOnceAsync(Callers, _ => new ServerLedger(_folder).StopOrphans());

        Assert.All(results, result => Assert.Null(result.Error));
        Assert.Empty(Entries());
    }

    [Fact]
    public async Task An_orphan_still_running_is_stopped_by_one_caller_only()
    {
        var server = StartIdleProcess();
        Plant(server.Id, server.StartTime.ToUniversalTime().Ticks, ownerProcessId: 2_100_000, ownerStartTicks: 1);

        var results = await AtOnceAsync(Callers, _ => new ServerLedger(_folder).StopOrphans());

        Assert.All(results, result => Assert.Null(result.Error));
        Assert.Equal(1, results.Sum(result => result.Value));
        Assert.True(server.WaitForExit(TimeSpan.FromSeconds(10)));
        Assert.Empty(Entries());
    }

    [Fact]
    public void A_process_that_is_not_the_recorded_one_is_never_stopped()
    {
        var bystander = StartIdleProcess();
        Plant(bystander.Id, bystander.StartTime.ToUniversalTime().Ticks - 1, ownerProcessId: 2_100_000, ownerStartTicks: 1);

        var stopped = new ServerLedger(_folder).StopOrphans();

        Assert.Equal(0, stopped);
        Assert.False(bystander.HasExited);
        Assert.Empty(Entries());
    }

    [Fact]
    public void A_server_whose_owner_is_running_is_left_alone()
    {
        var server = StartIdleProcess();
        using var owner = Process.GetCurrentProcess();
        Plant(server.Id, server.StartTime.ToUniversalTime().Ticks, owner.Id, owner.StartTime.ToUniversalTime().Ticks);

        var stopped = new ServerLedger(_folder).StopOrphans();

        Assert.Equal(0, stopped);
        Assert.False(server.HasExited);
        Assert.Single(Entries());
    }

    [Fact]
    public async Task Forgetting_a_server_while_others_look_for_orphans_leaves_no_stale_entry_and_throws_nothing()
    {
        using var owner = Process.GetCurrentProcess();
        var ownerId = owner.Id;
        var ownerTicks = owner.StartTime.ToUniversalTime().Ticks;
        var ledger = new ServerLedger(_folder);
        const int Rounds = 5;
        const int Servers = 200;

        for (var round = 0; round < Rounds; round++)
        {
            var ids = Enumerable.Range(0, Servers).Select(server => 3_000_000 + (round * Servers) + server).ToList();
            foreach (var id in ids)
            {
                Plant(id, serverStartTicks: 1, ownerId, ownerTicks);
            }

            using var done = new CancellationTokenSource();
            var watchers = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(
                () =>
                {
                    while (!done.IsCancellationRequested)
                    {
                        ledger.StopOrphans();
                    }
                },
                TaskCreationOptions.LongRunning)).ToList();
            try
            {
                var forgetting = await AtOnceAsync(2, caller => ids.Where((_, index) => index % 2 == caller).Sum(id =>
                {
                    ledger.Forget(id);
                    return 1;
                }));

                Assert.All(forgetting, result => Assert.Null(result.Error));
            }
            finally
            {
                done.Cancel();
                await Task.WhenAll(watchers);
            }

            Assert.Empty(Entries());
        }
    }

    private void Plant(int serverProcessId, long serverStartTicks, int ownerProcessId, long ownerStartTicks)
    {
        var entry = new { ServerProcessId = serverProcessId, ServerStartTicks = serverStartTicks, OwnerProcessId = ownerProcessId, OwnerStartTicks = ownerStartTicks };
        File.WriteAllText(Path.Combine(_folder, $"{serverProcessId}.json"), JsonSerializer.Serialize(entry));
    }

    private string[] Entries() => Directory.GetFiles(_folder);

    private Process StartIdleProcess()
    {
        // Waits for input that never comes, so it lives until it is stopped.
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c pause")
            : new ProcessStartInfo("cat");
        info.UseShellExecute = false;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.CreateNoWindow = true;
        var process = Process.Start(info) ?? throw new InvalidOperationException("The idle process did not start.");
        _started.Add(process);
        return process;
    }

    /// <summary>Runs <paramref name="work"/> on <paramref name="callers"/> threads released together, and returns what each gave or threw.</summary>
    private static async Task<List<(int Value, Exception? Error)>> AtOnceAsync(int callers, Func<int, int> work)
    {
        using var start = new Barrier(callers);
        var tasks = Enumerable.Range(0, callers).Select(caller => Task.Factory.StartNew(
            () =>
            {
                start.SignalAndWait();
                try
                {
                    return (work(caller), (Exception?)null);
                }
                catch (Exception error)
                {
                    return (0, error);
                }
            },
            TaskCreationOptions.LongRunning)).ToArray();
        return (await Task.WhenAll(tasks)).ToList();
    }
}
