using Weir.Core.Json;
using Weir.Core.Time;
using Weir.Infrastructure.Settings;

namespace Weir.Infrastructure.Tests.Settings;

/// <summary>When a value in a backup is the value already stored, so a restore rewrites only what differs.</summary>
public sealed class ConfigurationBundleValueTests
{
    private static readonly DateTime Noon = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static WireTimestampValue Aware(DateTime utc) => new(Timestamp.FromUtc(utc));

    private static WireTimestampValue Naive(DateTime clock) => new(Timestamp.Naive(clock));

    public static TheoryData<bool, WireValue?, WireValue, bool> Times() => new()
    {
        { true, null, WireNull.Instance, true },
        { true, WireNull.Instance, Aware(Noon), false },
        { true, Aware(Noon), WireNull.Instance, false },
        { true, Aware(Noon), Aware(Noon), true },
        { true, Aware(Noon), Aware(Noon.AddSeconds(1)), false },
        { true, Naive(Noon), Naive(Noon), true },
        { true, Naive(Noon), Naive(Noon.AddSeconds(1)), false },
        { true, Naive(Noon), Aware(Noon), false },
        { false, Aware(Noon), Aware(Noon), true },
    };

    [Theory]
    [MemberData(nameof(Times))]
    public void Two_times_are_the_same_only_when_both_are_the_same_kind_of_time_and_the_same_moment(
        bool timeColumn, WireValue? stored, WireValue assigned, bool same)
    {
        Assert.Equal(same, ConfigurationBundleStore.ValuesEqual(timeColumn ? ConfigurationBundleStore.ColumnKind.DateTime : ConfigurationBundleStore.ColumnKind.Raw, stored, assigned));
    }

    [Fact]
    public void Two_values_that_are_not_times_are_the_same_by_text_or_by_number()
    {
        Assert.True(ConfigurationBundleStore.ValuesEqual(ConfigurationBundleStore.ColumnKind.Raw, null, WireNull.Instance));
        Assert.True(ConfigurationBundleStore.ValuesEqual(ConfigurationBundleStore.ColumnKind.Raw, new WireString("a"), new WireString("a")));
        Assert.False(ConfigurationBundleStore.ValuesEqual(ConfigurationBundleStore.ColumnKind.Raw, new WireString("a"), new WireString("b")));
        Assert.True(ConfigurationBundleStore.ValuesEqual(ConfigurationBundleStore.ColumnKind.Raw, new WireInteger(1), WireBool.True));
        Assert.False(ConfigurationBundleStore.ValuesEqual(ConfigurationBundleStore.ColumnKind.Raw, new WireString("1"), new WireInteger(1)));
    }
}
