using Xunit;

namespace Weir.Tray.Tests;

/// <summary>Joining the input of the thread that holds the foreground: when it is allowed, when Windows refuses, and that the join is always undone.</summary>
public sealed class ForegroundHolderTests
{
    private sealed class RecordingThreads(uint holder, uint current, bool allowsJoin, bool failsToRaise = false) : IInputThreads
    {
        public List<string> Calls { get; } = [];

        public uint HolderThread() => holder;

        public uint CurrentThread() => current;

        public bool Join(uint thread, uint holderThread)
        {
            Calls.Add($"join {thread} {holderThread}");
            return allowsJoin;
        }

        public void Leave(uint thread, uint holderThread) => Calls.Add($"leave {thread} {holderThread}");

        public void RequestForeground(IntPtr window)
        {
            Calls.Add($"foreground {window}");
            if (failsToRaise)
            {
                throw new InvalidOperationException("Windows refused.");
            }
        }
    }

    [Fact]
    public void The_foreground_is_asked_for_while_joined_to_the_holder_and_the_join_is_undone()
    {
        var threads = new RecordingThreads(holder: 7, current: 3, allowsJoin: true);

        ForegroundHolder.Take(new IntPtr(9), threads);

        Assert.Equal(["join 3 7", "foreground 9", "leave 3 7"], threads.Calls);
    }

    [Theory]
    [InlineData(0u, 3u, true)]
    [InlineData(3u, 3u, true)]
    [InlineData(7u, 3u, false)]
    public void Nothing_is_asked_when_no_window_is_in_front_the_holder_is_this_thread_or_the_join_is_refused(uint holder, uint current, bool allowsJoin)
    {
        var threads = new RecordingThreads(holder, current, allowsJoin);

        ForegroundHolder.Take(new IntPtr(9), threads);

        Assert.DoesNotContain(threads.Calls, c => c.StartsWith("foreground", StringComparison.Ordinal) || c.StartsWith("leave", StringComparison.Ordinal));
    }

    [Fact]
    public void The_join_is_undone_even_when_asking_for_the_foreground_fails()
    {
        var threads = new RecordingThreads(holder: 7, current: 3, allowsJoin: true, failsToRaise: true);

        Assert.Throws<InvalidOperationException>(() => ForegroundHolder.Take(new IntPtr(9), threads));

        Assert.Equal(["join 3 7", "foreground 9", "leave 3 7"], threads.Calls);
    }
}
