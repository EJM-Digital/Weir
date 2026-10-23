
namespace Weir.Infrastructure.Tests.Processing;

/// <summary>
/// What the folder watcher relies on from System.IO.FileSystemWatcher, run on the operating system the tests run on: the
/// 64 KiB buffer is accepted before the watcher is started, and stopping or disposing a watcher that is already stopped or
/// disposed raises nothing. The service has no catch for either, so a runtime that began to throw would show here first.
/// </summary>
public sealed class FileSystemWatcherContractTests
{
    [Fact]
    public void The_buffer_the_service_asks_for_is_accepted_before_the_watcher_starts_and_another_while_it_runs()
    {
        using var home = new TempDirectory();
        using var watcher = new FileSystemWatcher(home.Path);

        watcher.InternalBufferSize = 65536;
        Assert.Equal(65536, watcher.InternalBufferSize);

        watcher.EnableRaisingEvents = true;
        watcher.InternalBufferSize = 32768;
        Assert.True(watcher.EnableRaisingEvents);
    }

    [Fact]
    public void A_watcher_can_be_stopped_again_after_it_was_stopped_and_after_it_was_disposed()
    {
        using var home = new TempDirectory();
        var watcher = new FileSystemWatcher(home.Path) { InternalBufferSize = 65536, EnableRaisingEvents = true };

        watcher.EnableRaisingEvents = false;
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();

        Assert.False(watcher.EnableRaisingEvents);
    }

    [Fact]
    public void A_running_watcher_that_is_disposed_can_still_be_told_to_stop()
    {
        using var home = new TempDirectory();
        var watcher = new FileSystemWatcher(home.Path) { InternalBufferSize = 65536, EnableRaisingEvents = true };

        watcher.Dispose();
        watcher.EnableRaisingEvents = false;

        Assert.False(watcher.EnableRaisingEvents);
    }
}
