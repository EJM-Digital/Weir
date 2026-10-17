using Weir.Infrastructure.Activity;

namespace Weir.Infrastructure.Tests.Activity;

/// <summary>What components say changed, and who hears it: every open stream, once, after it started listening.</summary>
public sealed class DataChangePublisherTests
{
    private static async Task<string> NextAsync(BroadcastSubscription<string> subscription)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var topic in subscription.ReadAllAsync(timeout.Token))
        {
            return topic;
        }

        throw new InvalidOperationException("The subscription ended.");
    }

    [Fact]
    public async Task Every_open_stream_hears_a_published_topic()
    {
        var publisher = new DataChangePublisher();
        using var first = publisher.Subscribe();
        using var second = publisher.Subscribe();

        publisher.Publish(DataTopics.Libraries);

        Assert.Equal((DataTopics.Libraries, DataTopics.Libraries), (await NextAsync(first), await NextAsync(second)));
    }

    [Fact]
    public async Task A_stream_does_not_hear_what_was_published_before_it_opened()
    {
        var publisher = new DataChangePublisher();
        publisher.Publish(DataTopics.Settings);
        using var stream = publisher.Subscribe();

        publisher.Publish(DataTopics.Jobs);

        Assert.Equal(DataTopics.Jobs, await NextAsync(stream));
    }

    [Fact]
    public void It_knows_whether_any_stream_is_open()
    {
        var publisher = new DataChangePublisher();
        Assert.False(publisher.HasListeners);

        var stream = publisher.Subscribe();
        Assert.True(publisher.HasListeners);

        stream.Dispose();
        Assert.False(publisher.HasListeners);
    }

    [Fact]
    public void Publishing_with_nobody_listening_is_harmless()
    {
        new DataChangePublisher().Publish(DataTopics.Metrics);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_topic_must_be_named(string? topic)
    {
        Assert.ThrowsAny<ArgumentException>(() => new DataChangePublisher().Publish(topic!));
    }

    [Fact]
    public void The_topics_are_the_snake_case_names_the_web_app_maps()
    {
        string[] topics = [.. typeof(DataTopics).GetFields().Select(field => (string)field.GetRawConstantValue()!)];

        Assert.Equal(
            [
                "backups", "connections", "direct_play_devices", "files_at_once", "folder_checks", "jobs", "kept_files", "libraries", "library_scan",
                "maintenance", "metrics", "network_access", "pause", "readiness", "sessions", "settings", "update",
            ],
            [.. topics.Order(StringComparer.Ordinal)]);
    }
}
