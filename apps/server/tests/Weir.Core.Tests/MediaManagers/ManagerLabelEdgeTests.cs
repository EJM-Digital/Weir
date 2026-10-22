using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>Labels for kinds Weir has no name for, manager values read from a payload, and the error a manager answer carries.</summary>
public sealed class ManagerLabelEdgeTests
{
    [Theory]
    [InlineData(null, "Media manager")]
    [InlineData("", "Media manager")]
    [InlineData("  RADARR ", "Radarr")]
    [InlineData("lidarr", "Media manager")]
    public void A_kind_is_labelled_by_its_product_or_generically(string? kind, string expected) =>
        Assert.Equal(expected, MediaManagerKinds.ProductLabel(kind));

    [Fact]
    public void A_manager_error_carries_the_status_it_answered_with()
    {
        var error = new MediaManagerHttpException("refused", 401);

        Assert.Equal(("refused", 401), (error.Message, error.StatusCode));
    }

    [Fact]
    public void Values_are_read_from_a_payload_and_capitalised_for_display()
    {
        var payload = (WireObject)WireJsonParser.Parse("""{"title":"a film"}""");

        Assert.Equal("a film", ((WireString)ManagerValues.Get(payload, "title")!).Value);
        Assert.Null(ManagerValues.Get(payload, "year"));
        Assert.Equal(string.Empty, ManagerValues.Capitalize(string.Empty));
        Assert.Equal("Radarr", ManagerValues.Capitalize("rADARR"));
    }

    [Fact]
    public void Only_a_manager_that_reported_has_answered()
    {
        var connection = new ManagerConnection("deluno", "Main", "http://m", "k");

        Assert.True(new ManagerLibraryTruth(connection, SignalStatus.Reported, []).IsReported);
        Assert.False(new ManagerLibraryTruth(connection, SignalStatus.Unreachable, []).IsReported);
    }
}
