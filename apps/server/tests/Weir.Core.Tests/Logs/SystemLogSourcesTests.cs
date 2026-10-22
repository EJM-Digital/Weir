using Weir.Core.Logs;

namespace Weir.Core.Tests.Logs;

/// <summary>The three places a system log row can come from, by their wire names.</summary>
public sealed class SystemLogSourcesTests
{
    [Theory]
    [InlineData("event", SystemLogSource.Event)]
    [InlineData("job", SystemLogSource.Job)]
    [InlineData("server", SystemLogSource.Server)]
    public void A_source_round_trips_through_its_wire_name(string name, SystemLogSource source)
    {
        Assert.True(SystemLogSources.TryParse(name, out var parsed));
        Assert.Equal(source, parsed);
        Assert.Equal(name, SystemLogSources.NameOf(source));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Event")]
    [InlineData("other")]
    public void Any_other_name_is_not_a_source(string? name) =>
        Assert.False(SystemLogSources.TryParse(name, out _));
}
