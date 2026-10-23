using Microsoft.Extensions.Logging;
using Weir.Core.Processing;
using Weir.Infrastructure.Processing.DirectPlay;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>An operator's own list of devices, read from Weir's data folder: how little a device must say, what a list may leave out and what is said when it cannot be read.</summary>
public sealed class DeviceProfileLoaderTests : IDisposable
{
    private readonly TempDirectory _home = new();

    public void Dispose() => _home.Dispose();

    private void WriteList(string json) => File.WriteAllText(_home.Join(DirectPlayEvaluation.OverrideFileName), json);

    [Fact]
    public void A_device_needs_only_a_name_and_says_nothing_it_has_not_been_told()
    {
        WriteList("""{"devices":[{"id":"box","name":"Box"}]}""");

        var device = Assert.Single(DeviceProfileLoader.Load(_home.Path));

        Assert.Equal(("box", "Box", string.Empty, string.Empty), (device.Id, device.Name, device.Source, device.Note));
        Assert.Empty(device.ContainersYes);
        Assert.Empty(device.AudioMaybe);
        Assert.Empty(device.Video);
    }

    [Fact]
    public void Texts_a_list_gives_as_null_are_read_as_empty()
    {
        WriteList("""{"devices":[{"id":null,"name":null,"source":null,"note":null,"containers":{"yes":[null,"mkv"],"maybe":"mp4"},"audio":[],"video":{"hevc":{"max_height":2160,"max_bit_depth":10,"containers":[null,"mkv"]},"av1":{"max_height":"tall"}},"video_maybe":[]}]}""");

        var device = Assert.Single(DeviceProfileLoader.Load(_home.Path));

        Assert.Equal((string.Empty, string.Empty, string.Empty, string.Empty), (device.Id, device.Name, device.Source, device.Note));
        Assert.Equal(new[] { string.Empty, "mkv" }.Order(StringComparer.Ordinal), device.ContainersYes.Order(StringComparer.Ordinal));
        Assert.Empty(device.ContainersMaybe);
        Assert.Equal((2160, 10), (device.Video["hevc"].MaxHeight, device.Video["hevc"].MaxBitDepth));
        Assert.Equal(new[] { string.Empty, "mkv" }.Order(StringComparer.Ordinal), device.Video["hevc"].Containers!.Order(StringComparer.Ordinal));
        Assert.Null(device.Video["av1"].MaxHeight);
        Assert.Null(device.Video["av1"].Containers);
        Assert.Empty(device.VideoMaybe);
    }

    [Fact]
    public void A_list_that_cannot_be_read_is_ignored_and_the_shipped_devices_are_used_and_the_reason_is_logged()
    {
        WriteList("""{"devices":[{"name":"No id"}]}""");
        var log = new CapturingLogger<DeviceProfileLoaderTests>();

        var devices = DeviceProfileLoader.Load(_home.Path, log);

        Assert.NotEmpty(devices);
        Assert.DoesNotContain(devices, device => device.Name == "No id");
        Assert.True(log.Logged(LogLevel.Warning, "not a readable device list"));
    }

    [Fact]
    public void A_list_that_cannot_be_read_is_ignored_without_a_log_to_say_so()
    {
        WriteList("not json at all");

        Assert.NotEmpty(DeviceProfileLoader.Load(_home.Path));
    }

    [Fact]
    public void A_list_already_read_is_not_read_again_until_the_file_changes()
    {
        WriteList("""{"devices":[{"id":"one","name":"One"}]}""");
        var first = DeviceProfileLoader.Load(_home.Path);

        Assert.Same(first, DeviceProfileLoader.Load(_home.Path));
    }
}
