using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Weir.Infrastructure.Tests;

/// <summary>
/// Reports, for the whole test run, when the shared thread pool was slow to begin work, so that a slow stretch can be laid
/// beside the start and end times of the tests that ran in it (the trx file) and the tests that cause it found and fixed. Off
/// unless <c>WEIR_POOL_SAMPLE</c> names a file to write; it only reports and never fails a test.
/// </summary>
/// <remarks>
/// The sampling runs on a thread of its own, so a starved pool cannot stop it. Several times a second it queues a piece of work
/// that does nothing and notes how long that waited to begin, and while one has not begun it keeps looking, so a stall is
/// written when it is over with the whole of its length. A line is written only for a wait of <see cref="Report"/> or more:
/// <c>start-UTC, end-UTC, wait in ms, pool threads, pending work, longest oversleep in ms</c>. The oversleep is how much later than
/// asked the sampler's own thread woke: when that is large too, the whole machine was too busy to run threads, and the pool is not
/// to blame.
/// </remarks>
internal static class PoolStarvationSampler
{
    internal const string FileVariable = "WEIR_POOL_SAMPLE";

    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Report = TimeSpan.FromMilliseconds(500);

    // The run starts its sampler before any test: the attribute is how a test assembly runs code once at load.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void StartIfAsked()
    {
        var path = Environment.GetEnvironmentVariable(FileVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        new Thread(() => Sample(path)) { IsBackground = true, Name = nameof(PoolStarvationSampler) }.Start();
    }

    private static void Sample(string path)
    {
        using var output = new StreamWriter(path, append: true) { AutoFlush = true };
        while (true)
        {
            var queuedAt = DateTimeOffset.UtcNow;
            var clock = Stopwatch.StartNew();
            var begun = 0;
            ThreadPool.UnsafeQueueUserWorkItem(_ => Volatile.Write(ref begun, 1), null);
            var threadsWhileWaiting = 0;
            var pendingWhileWaiting = 0L;
            var oversleep = TimeSpan.Zero;
            while (Volatile.Read(ref begun) == 0)
            {
                var slept = Stopwatch.StartNew();
                Thread.Sleep(20);
                var late = slept.Elapsed - TimeSpan.FromMilliseconds(20);
                if (late > oversleep)
                {
                    oversleep = late;
                }
                if (clock.Elapsed >= Report)
                {
                    threadsWhileWaiting = Math.Max(threadsWhileWaiting, ThreadPool.ThreadCount);
                    pendingWhileWaiting = Math.Max(pendingWhileWaiting, ThreadPool.PendingWorkItemCount);
                }
            }

            if (clock.Elapsed >= Report)
            {
                output.WriteLine(string.Join(
                    ',',
                    queuedAt.ToString("O", CultureInfo.InvariantCulture),
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ((long)clock.Elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
                    threadsWhileWaiting.ToString(CultureInfo.InvariantCulture),
                    pendingWhileWaiting.ToString(CultureInfo.InvariantCulture),
                    ((long)oversleep.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)));
            }

            Thread.Sleep(Every);
        }
    }
}
