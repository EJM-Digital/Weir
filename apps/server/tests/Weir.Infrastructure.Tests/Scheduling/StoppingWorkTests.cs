using Weir.Infrastructure.Scheduling;

namespace Weir.Infrastructure.Tests.Scheduling;

/// <summary>A background service's work ends quietly when the server stops, and in no other way hides a failure.</summary>
public sealed class StoppingWorkTests
{
    [Fact]
    public async Task Work_that_ends_by_itself_ends_the_run()
    {
        var ran = false;

        await StoppingWork.RunAsync(() => { ran = true; return Task.CompletedTask; }, CancellationToken.None);

        Assert.True(ran);
    }

    [Fact]
    public async Task A_cancellation_caused_by_the_stop_ends_the_run_without_an_error()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await StoppingWork.RunAsync(() => Task.FromCanceled(stopping.Token), stopping.Token);
    }

    [Fact]
    public async Task A_cancellation_nobody_asked_for_is_still_an_error()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => StoppingWork.RunAsync(() => throw new OperationCanceledException(), CancellationToken.None));
    }

    [Fact]
    public async Task Any_other_failure_is_still_an_error()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => StoppingWork.RunAsync(() => throw new InvalidOperationException(), stopping.Token));
    }

    [Fact]
    public async Task Work_must_be_given()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => StoppingWork.RunAsync(null!, CancellationToken.None));
    }
}
