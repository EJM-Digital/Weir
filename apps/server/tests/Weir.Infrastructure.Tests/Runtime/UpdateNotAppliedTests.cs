using Weir.Core.Json;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>While the tray holds an update back for want of a copy of Weir's data, the update state says why (#951).</summary>
public sealed class UpdateNotAppliedTests : IDisposable
{
    private readonly StoreFixture _store = new();

    public void Dispose() => _store.Dispose();

    private string Path_(string name) => Path.Join(_store.Options.WeirHome, name);

    [Fact]
    public void The_state_says_why_the_update_was_not_applied()
    {
        File.WriteAllText(Path_(UpdateFiles.StateFileName), "{\"downloaded\": true, \"version\": \"9.9.9\"}");
        File.WriteAllText(Path_(UpdateFiles.NotAppliedFileName), "{\"version\": \"9.9.9\", \"reason\": \"The drive is full. Free up space, then try again.\"}");

        var state = new UpdateFiles(_store.Options).ReadState();

        Assert.Equal(
            "{\"downloaded\":true,\"pending_version\":\"9.9.9\",\"state\":\"downloaded\",\"failure\":null,\"tray_running\":false,\"not_updated_reason\":\"The drive is full. Free up space, then try again.\"}",
            WireJsonWriter.Dumps(state, WireJsonFormat.Response));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("{\"version\": \"9.9.9\"}")]
    [InlineData("{\"reason\": \"\"}")]
    [InlineData("{\"reason\": 4}")]
    [InlineData("[]")]
    public void No_reason_is_said_when_there_is_none_to_read(string? contents)
    {
        if (contents is not null)
        {
            File.WriteAllText(Path_(UpdateFiles.NotAppliedFileName), contents);
        }

        var state = new UpdateFiles(_store.Options).ReadState();

        Assert.Equal(
            "{\"downloaded\":false,\"pending_version\":null,\"state\":\"idle\",\"failure\":null,\"tray_running\":false}",
            WireJsonWriter.Dumps(state, WireJsonFormat.Response));
    }

    [Fact]
    public void A_reason_that_is_refused_for_a_moment_is_still_said()
    {
        File.WriteAllText(Path_(UpdateFiles.NotAppliedFileName), "{\"version\": \"9.9.9\", \"reason\": \"The drive is full.\"}");
        var files = new UpdateFiles(_store.Options);
        Assert.Equal("The drive is full.", ((WireString)files.ReadState()["not_updated_reason"]).Value);

        using (new FileStream(Path_(UpdateFiles.NotAppliedFileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal("The drive is full.", ((WireString)files.ReadState()["not_updated_reason"]).Value);
        }
    }
}
