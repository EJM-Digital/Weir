namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>One <see cref="RecordedWorld"/> for a class whose tests only ask Weir questions and are refused, so none changes what the next sees.</summary>
public sealed class RecordedWorldFixture : IAsyncLifetime
{
    private RecordedWorld? _world;

    internal RecordedWorld World => _world ?? throw new InvalidOperationException("The server has not started yet.");

    public async Task InitializeAsync() => _world = await RecordedWorld.StartAsync();

    public async Task DisposeAsync()
    {
        if (_world is not null)
        {
            await _world.DisposeAsync();
        }
    }
}
