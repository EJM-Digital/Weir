using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Infrastructure.Processing;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>Reading a stored record's detail and writing the text a person attaches to a bug report.</summary>
public sealed class FileLogRenderingTests
{
    private static readonly FileLogStore Store = new();

    private static ProcessingFileLogRecord Record(string detail, string outcome = "done", string title = "", string library = "") => new()
    {
        RelativePath = "Films/A.mkv",
        LibraryName = library,
        Outcome = outcome,
        Title = title,
        DetailJson = detail,
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_record_with_no_detail_has_an_empty_one(string? raw)
    {
        Assert.Empty(Store.ParseDetail(raw).Keys);
    }

    [Fact]
    public void A_detail_that_is_an_object_is_kept_as_it_is()
    {
        var detail = Store.ParseDetail("""{"outcome":"done","count":3}""");

        Assert.Equal(["count", "outcome"], detail.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("42")]
    [InlineData("\"words\"")]
    public void A_detail_that_is_not_an_object_is_kept_under_one_name(string raw)
    {
        var detail = Store.ParseDetail(raw);

        Assert.Equal(["detail"], detail.Keys);
    }

    [Fact]
    public void A_detail_that_is_not_json_is_kept_as_the_text_it_was()
    {
        var detail = Store.ParseDetail("{broken");

        Assert.Equal("{broken", Assert.IsType<WireString>(detail["unparsed_detail"]).Value);
    }

    [Fact]
    public void With_no_records_the_text_says_so()
    {
        Assert.Equal("Weir has no retained processing records for this file.\n", Store.RenderLogText([], 0));
    }

    [Fact]
    public void Every_kind_of_value_is_written_in_plain_words()
    {
        var text = Store.RenderLogText(
            [Record("""{"a_text":"words","b_whole":7,"c_fraction":2.5,"d_yes":true,"e_no":false,"f_nothing":null,"g_object":{"k":1},"h_list":[1,2]}""")],
            retained: 1);

        Assert.Contains("a_text: words\n", text, StringComparison.Ordinal);
        Assert.Contains("b_whole: 7\n", text, StringComparison.Ordinal);
        Assert.Contains("c_fraction: 2.5\n", text, StringComparison.Ordinal);
        Assert.Contains("d_yes: True\n", text, StringComparison.Ordinal);
        Assert.Contains("e_no: False\n", text, StringComparison.Ordinal);
        Assert.Contains("f_nothing: None\n", text, StringComparison.Ordinal);
        Assert.Contains("g_object: {\"k\":1}\n", text, StringComparison.Ordinal);
        Assert.Contains("h_list: [1,2]\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_names_the_file_the_workflow_and_each_pass()
    {
        var text = Store.RenderLogText(
            [Record("{}", outcome: "", title: "Kept the original", library: "Films"), Record("{}", outcome: "failed")],
            retained: 2);

        Assert.Contains("processing record for Films/A.mkv", text, StringComparison.Ordinal);
        Assert.Contains("Workflow: Films", text, StringComparison.Ordinal);
        Assert.Contains("Records retained: 2 (newest first)", text, StringComparison.Ordinal);
        Assert.Contains("no outcome recorded", text, StringComparison.Ordinal);
        Assert.Contains("Kept the original", text, StringComparison.Ordinal);
        Assert.Contains("  —  failed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void When_more_are_kept_than_shown_the_text_says_it_shows_the_newest()
    {
        var text = Store.RenderLogText([Record("{}")], retained: 1200);

        Assert.Contains("Showing the newest 1 of 1,200 entries", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Workflow:", text, StringComparison.Ordinal);
    }
}
