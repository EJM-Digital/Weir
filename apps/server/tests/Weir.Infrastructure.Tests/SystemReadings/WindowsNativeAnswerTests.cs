using Weir.Infrastructure.SystemReadings;

namespace Weir.Infrastructure.Tests.SystemReadings;

/// <summary>What Windows' yes or no about the machine's load means, run on any system: a refusal reads as no reading, never an error.</summary>
public sealed class WindowsNativeAnswerTests
{
    [Fact]
    public void The_processor_time_is_read_only_when_Windows_gave_it()
    {
        Assert.Equal(new CpuTimes(10, 70), WindowsNative.CpuTimesFrom(succeeded: true, idle: 10, kernel: 30, user: 40));
        Assert.Null(WindowsNative.CpuTimesFrom(succeeded: false, idle: 10, kernel: 30, user: 40));
    }

    [Fact]
    public void The_memory_is_read_only_when_Windows_gave_it()
    {
        Assert.Equal(new MemoryReading(1000, 250), WindowsNative.MemoryFrom(succeeded: true, total: 1000, available: 250));
        Assert.Null(WindowsNative.MemoryFrom(succeeded: false, total: 1000, available: 250));
    }

    [Fact]
    public void A_volumes_load_is_read_only_when_Windows_gave_it()
    {
        Assert.Equal(new DiskCounters(1, 2, 3, 4), WindowsNative.CountersFrom(succeeded: true, 1, 2, 3, 4));
        Assert.Null(WindowsNative.CountersFrom(succeeded: false, 1, 2, 3, 4));
    }
}
