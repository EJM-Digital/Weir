using System.Text;
using FsCheck.Fluent;
using Weir.Core.Json;
using Weir.TestSupport;

namespace Weir.Core.Tests.Json;

/// <summary>
/// The JSON the API reads and writes, over values of every shape: what is written reads back as the same value in every layout and
/// every encoding, and text that is not JSON is refused with a position rather than breaking the parser.
/// </summary>
public sealed class WireJsonPropertyTests
{
    private static readonly WireJsonFormat[] Formats =
        [WireJsonFormat.Response, WireJsonFormat.Default, WireJsonFormat.IndentedSorted, WireJsonFormat.Indented, WireJsonFormat.Compact];

    private static readonly string[] EditPieces = ["{", "}", "[", "]", ",", ":", "\"", "\\", "-", "0", "1", "e", ".", "t", "n", "N", "I", " ", "\u0000", "\n", "é", "\ud800"];

    [Fact]
    public void Written_json_reads_back_as_the_same_value_in_every_layout()
    {
        PropertyChecks.ForAll(WireValueGenerators.Value(3, loneSurrogates: true).Zip(Gen.Elements(Formats)), found =>
        {
            var (value, format) = found;
            var text = WireJsonWriter.Dumps(value, format);
            Assert.True(WireValueGenerators.Same(value, WireJsonParser.Parse(text), keysSorted: format.SortKeys), text);
        });
    }

    [Fact]
    public void Written_json_reads_back_from_bytes_in_every_encoding_the_parser_detects()
    {
        var encodings = new Encoding[]
        {
            new UTF8Encoding(false), new UTF8Encoding(true), new UnicodeEncoding(false, false), new UnicodeEncoding(false, true),
            new UnicodeEncoding(true, false), new UnicodeEncoding(true, true), new UTF32Encoding(false, false), new UTF32Encoding(false, true),
            new UTF32Encoding(true, false), new UTF32Encoding(true, true),
        };

        PropertyChecks.ForAll(WireValueGenerators.Value(3).Zip(Gen.Elements(encodings)), found =>
        {
            var (value, encoding) = found;
            var text = WireJsonWriter.Dumps(value, WireJsonFormat.Compact);
            byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
            Assert.True(WireValueGenerators.Same(value, WireJsonParser.ParseBytes(bytes)), $"{encoding.EncodingName}: {text}");
        });
    }

    [Fact]
    public void Writing_what_was_read_gives_the_same_text_again()
    {
        PropertyChecks.ForAll(WireValueGenerators.Value(3, loneSurrogates: true).Zip(Gen.Elements(Formats)), found =>
        {
            var (value, format) = found;
            var once = WireJsonWriter.Dumps(value, format);
            Assert.Equal(once, WireJsonWriter.Dumps(WireJsonParser.Parse(once), format));
        });
    }

    [Fact]
    public void Every_double_is_written_as_text_that_reads_back_as_the_same_double()
    {
        PropertyChecks.ForAll(WireValueGenerators.AnyDouble, number =>
        {
            var text = WireJsonWriter.FloatRepr(number);
            var read = Assert.IsType<WireNumber>(WireJsonParser.Parse(text)).Value;
            Assert.True(
                double.IsNaN(number) ? double.IsNaN(read) : BitConverter.DoubleToInt64Bits(number) == BitConverter.DoubleToInt64Bits(read),
                text);
        });
    }

    [Fact]
    public void Json_with_one_edit_is_read_or_refused_with_a_position_inside_the_text()
    {
        var edited =
            from value in WireValueGenerators.Value(3)
            let text = WireJsonWriter.Dumps(value, WireJsonFormat.Compact)
            from position in Gen.Choose(0, text.Length)
            from edit in Gen.Elements(Edit.Delete, Edit.Insert, Edit.Replace)
            from piece in Gen.Elements(EditPieces)
            select Apply(text, position, edit, piece);

        PropertyChecks.ForAll(edited, text =>
        {
            try
            {
                var value = WireJsonParser.Parse(text);
                Assert.True(WireValueGenerators.Same(value, WireJsonParser.Parse(WireJsonWriter.Dumps(value, WireJsonFormat.Compact))));
            }
            catch (WireJsonDecodeException refusal)
            {
                Assert.InRange(refusal.Position, 0, text.Length);
                Assert.False(string.IsNullOrEmpty(refusal.Detail));
            }
        });
    }

    [Fact]
    public void Bytes_that_are_not_json_text_are_refused_as_decode_or_encoding_errors_and_nothing_else()
    {
        var bytes = Gen.Choose(0, 12).SelectMany(count => Gen.ArrayOf(Gen.Elements<byte>(0x00, 0x7b, 0x7d, 0x22, 0x31, 0x20, 0xef, 0xbb, 0xbf, 0xfe, 0xff, 0xc3, 0x28, 0xd8), count));

        PropertyChecks.ForAll(bytes, content =>
        {
            try
            {
                WireJsonParser.ParseBytes(content);
            }
            catch (Exception exception) when (exception is WireJsonDecodeException or WireJsonEncodingException)
            {
                // Refused with a reason a client can read.
            }
        });
    }

    private static string Apply(string text, int position, Edit edit, string piece) => edit switch
    {
        Edit.Delete when position < text.Length => text.Remove(position, 1),
        Edit.Replace when position < text.Length => text.Remove(position, 1).Insert(position, piece),
        _ => text.Insert(position, piece),
    };

    private enum Edit
    {
        Delete,
        Insert,
        Replace,
    }
}
