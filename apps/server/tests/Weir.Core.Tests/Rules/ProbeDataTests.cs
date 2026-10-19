using System.Text.Json;
using Weir.Core.Rules;

namespace Weir.Core.Tests.Rules;

/// <summary>Reading what ffprobe reported for a file and its streams, whose values are loosely typed.</summary>
public sealed class ProbeDataTests
{
    [Fact]
    public void A_refusal_to_read_probe_data_is_a_value_error_unless_it_names_another_kind()
    {
        var inner = new InvalidOperationException("inner");

        Assert.Equal("ValueError", new RulesInputException().ErrorKind);
        Assert.Equal("ValueError", new RulesInputException("bad").ErrorKind);
        Assert.Equal("bad", new RulesInputException("bad").Message);
        Assert.Same(inner, new RulesInputException("bad", inner).InnerException);
        Assert.Equal("ValueError", new RulesInputException("bad", inner).ErrorKind);
        Assert.Equal("KeyError", new RulesInputException("KeyError", "'index'").ErrorKind);
        Assert.NotNull(new RulesInputException().Message);
    }

    [Fact]
    public void A_stream_has_to_be_an_object()
    {
        using var document = JsonDocument.Parse("[1]");

        Assert.Throws<ArgumentException>(() => new ProbeStreamInfo(document.RootElement));
    }

    [Fact]
    public void A_stream_is_read_leniently()
    {
        var stream = ProbeStreamInfo.Parse("""
            {"index": "2", "codec_type": "  Audio ", "codec_name": "eac3",
             "tags": {"language": "eng", "title": "Main", "count": 3, "gone": null, "list": ["x"]},
             "disposition": {"default": 1, "forced": "0", "comment": "yes", "attached_pic": true}}
            """);

        Assert.Equal(2, stream.Index);
        Assert.Equal("audio", stream.CodecType);
        Assert.Equal("eac3", stream.CodecName);
        Assert.Equal(["language", "title"], stream.Tags.Keys);
        Assert.Equal("eng", stream.Tag("language"));
        Assert.Null(stream.Tag("count"));
        Assert.Null(stream.Tag("absent"));
        Assert.Equal(1, stream.Disposition["default"]);
        Assert.Equal(0, stream.Disposition["forced"]);
        Assert.Equal(1, stream.Disposition["attached_pic"]);
        Assert.DoesNotContain("comment", stream.Disposition.Keys);
        Assert.Equal(JsonValueKind.String, stream.Get("codec_name")!.Value.ValueKind);
    }

    [Fact]
    public void A_stream_with_nothing_set_reads_as_empty()
    {
        var stream = ProbeStreamInfo.Parse("""{"index": "x", "codec_type": 5, "tags": [], "disposition": 0}""");

        Assert.Null(stream.Index);
        Assert.Equal(string.Empty, stream.CodecType);
        Assert.Equal(string.Empty, stream.CodecName);
        Assert.Empty(stream.Tags);
        Assert.Empty(stream.Disposition);
        Assert.Empty(ProbeStreamInfo.Parse("{}").Tags);
        Assert.Empty(ProbeStreamInfo.Parse("{}").Disposition);
        Assert.Null(ProbeStreamInfo.Parse("{}").Index);
    }

    [Fact]
    public void The_streams_and_chapters_of_a_file_are_the_objects_in_their_lists_in_order()
    {
        var result = ProbeResult.Parse("""{"streams": [{"index": 0}, 5, null, {"index": 1}], "chapters": [{"id": 1}, "x", {"id": 2}]}""");

        Assert.Equal([0L, 1L], result.Streams.Select(stream => stream.Index!.Value));
        Assert.Equal([1, 2], result.Chapters.Select(chapter => chapter.GetProperty("id").GetInt32()));
    }

    [Fact]
    public void A_file_with_no_lists_or_that_is_not_an_object_has_no_streams_or_chapters()
    {
        foreach (var json in new[] { "{}", """{"streams": {}, "chapters": 3}""", "[]", "7" })
        {
            var result = ProbeResult.Parse(json);

            Assert.Empty(result.Streams);
            Assert.Empty(result.Chapters);
        }
    }

    [Fact]
    public void A_probe_result_keeps_its_own_copy_of_the_json()
    {
        using var document = JsonDocument.Parse("""{"streams": []}""");
        var result = new ProbeResult(document.RootElement);
        document.Dispose();

        Assert.Equal(JsonValueKind.Object, result.Json.ValueKind);
    }
}
