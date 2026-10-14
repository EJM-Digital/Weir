using System.Threading.Channels;

namespace Weir.Infrastructure.Tests;

/// <summary>
/// A task scheduler with exactly one thread, which is its own and not the thread pool's. A test that asks "does this code hold
/// a thread while it waits?" starts the code here: each piece of work gets the thread when the one before has given it up, so
/// code that held it while waiting would keep every later piece from ever starting, and code that gave it up lets them all
/// start. That answer does not depend on how busy the process's thread pool is, or on any clock, which is why it can be given
/// while every other test in the assembly runs.
/// </summary>
internal sealed class OneThreadScheduler : TaskScheduler, IDisposable
{
    private readonly Channel<Task> _queue = Channel.CreateUnbounded<Task>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Thread _thread;

    public OneThreadScheduler()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = nameof(OneThreadScheduler) };
        _thread.Start();
    }

    public override int MaximumConcurrencyLevel => 1;

    /// <summary>Lets the thread end once the work already queued has run. Work queued after that runs on the thread pool instead.</summary>
    public void Dispose() => _queue.Writer.TryComplete();

    protected override void QueueTask(Task task)
    {
        if (!_queue.Writer.TryWrite(task))
        {
            // Disposed while a continuation was still on its way here: it still has to run.
            ThreadPool.UnsafeQueueUserWorkItem(_ => TryExecuteTask(task), null);
        }
    }

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
        !taskWasPreviouslyQueued && Environment.CurrentManagedThreadId == _thread.ManagedThreadId && TryExecuteTask(task);

    // A channel cannot be listed without taking the work out of it.
    protected override IEnumerable<Task> GetScheduledTasks() => throw new NotSupportedException();

    private void Run()
    {
        // This thread is the one thing here that is meant to block while it has nothing to run.
        while (_queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (_queue.Reader.TryRead(out var task))
            {
                TryExecuteTask(task);
            }
        }
    }
}
