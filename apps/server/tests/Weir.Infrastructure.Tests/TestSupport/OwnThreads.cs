namespace Weir.Infrastructure.Tests;

/// <summary>
/// Starts work that has to be in flight at the same moment (callers that meet at a barrier before racing for a row), each piece
/// on a thread of its own. Pieces on the thread pool would each hold a pool thread while they waited for the others, and the pool
/// has only as many threads ready as the machine has processors (four on a CI runner) and adds one about every half second after
/// that: twenty-four callers at a barrier starve every other test in the process for as long as it takes to grow to twenty-four.
/// </summary>
internal static class OwnThreads
{
    public static Task RunAllAsync(int count, Func<int, Task> work) =>
        Task.WhenAll(Enumerable.Range(0, count).Select(index => Start(work, index)));

    public static Task<T[]> RunAllAsync<T>(int count, Func<int, Task<T>> work) =>
        Task.WhenAll(Enumerable.Range(0, count).Select(index => Start(work, index)));

    private static Task Start(Func<int, Task> work, int index) =>
        Task.Factory.StartNew(() => work(index), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private static Task<T> Start<T>(Func<int, Task<T>> work, int index) =>
        Task.Factory.StartNew(() => work(index), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
}
