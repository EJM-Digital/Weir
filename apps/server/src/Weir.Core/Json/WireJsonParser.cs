using System.Text;

namespace Weir.Core.Json;

/// <summary>JSON that does not parse: the error detail and the character position it was found at.</summary>
public sealed class WireJsonDecodeException : Exception
{
    public WireJsonDecodeException()
    {
        Detail = string.Empty;
    }

    public WireJsonDecodeException(string message)
        : base(message)
    {
        Detail = message;
    }

    public WireJsonDecodeException(string message, Exception innerException)
        : base(message, innerException)
    {
        Detail = message;
    }

    public WireJsonDecodeException(string detail, int position)
        : base($"{detail}: char {position}")
    {
        Detail = detail;
        Position = position;
    }

    /// <summary>The message without position, e.g. <c>Expecting value</c>.</summary>
    public string Detail { get; }

    public int Position { get; }
}

/// <summary>Bytes that are not valid text in the detected encoding.</summary>
public sealed class WireJsonEncodingException : Exception
{
    public WireJsonEncodingException()
    {
    }

    public WireJsonEncodingException(string message)
        : base(message)
    {
    }

    public WireJsonEncodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A JSON parser with fixed acceptance rules (including <c>NaN</c> and <c>Infinity</c>), error messages and
/// positions, so request bodies and data written by earlier releases parse the same way and clients and the
/// contract suite see the same validation errors.
/// </summary>
/// <remarks>
/// Split by concern: this file is byte decoding and the parser's entry points; <c>Scanner</c> dispatches one
/// value by its first character; <c>Numbers</c> and <c>Strings</c> scan those two literal shapes; <c>Structure</c>
/// scans objects and arrays (which call back into the scanner for each member's value).
/// </remarks>
public static partial class WireJsonParser
{
    /// <summary>
    /// The deepest nesting of arrays and objects a document may have. Parsing recurses once per level, so without a
    /// limit a small body of nested brackets overflows the stack and ends the process; no real payload comes close.
    /// </summary>
    public const int MaxDepth = 128;

    /// <summary>
    /// The most digits an integer may have. Parsing a big integer costs time that grows faster than its length, so
    /// a single huge number could tie up a request thread; no setting or payload Weir reads needs more than a few dozen.
    /// </summary>
    public const int MaxIntegerDigits = 4300;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Parses bytes: the encoding (UTF-8, UTF-16 or UTF-32, with or without a byte-order mark) is detected
    /// from the BOM or the pattern of zero bytes at the start, then the text is parsed.
    /// </summary>
    public static WireValue ParseBytes(ReadOnlySpan<byte> bytes) => Parse(DecodeBytes(bytes));

    public static string DecodeBytes(ReadOnlySpan<byte> b)
    {
        try
        {
            if (b.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }) || b.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
            {
                return new UTF32Encoding(bigEndian: b[0] == 0, byteOrderMark: true, throwOnInvalidCharacters: true).GetString(b[4..]);
            }

            if (b.StartsWith(new byte[] { 0xFE, 0xFF }) || b.StartsWith(new byte[] { 0xFF, 0xFE }))
            {
                return new UnicodeEncoding(bigEndian: b[0] == 0xFE, byteOrderMark: true, throwOnInvalidBytes: true).GetString(b[2..]);
            }

            if (b.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                return StrictUtf8.GetString(b[3..]);
            }

            if (b.Length >= 4)
            {
                if (b[0] == 0)
                {
                    return b[1] != 0
                        ? new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true).GetString(b)
                        : new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true).GetString(b);
                }

                if (b[1] == 0)
                {
                    return b[2] != 0 || b[3] != 0
                        ? new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true).GetString(b)
                        : new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true).GetString(b);
                }
            }
            else if (b.Length == 2)
            {
                if (b[0] == 0)
                {
                    return new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true).GetString(b);
                }

                if (b[1] == 0)
                {
                    return new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true).GetString(b);
                }
            }

            return StrictUtf8.GetString(b);
        }
        catch (DecoderFallbackException exception)
        {
            throw new WireJsonEncodingException("The body is not valid text.", exception);
        }
    }

    /// <summary>Parses one JSON value; surrounding whitespace is allowed, a leading BOM or trailing data is not.</summary>
    public static WireValue Parse(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.StartsWith('﻿'))
        {
            throw new WireJsonDecodeException("Unexpected UTF-8 BOM (decode using utf-8-sig)", 0);
        }

        var idx = SkipWhitespace(s, 0);
        var value = ScanOnceOrExpectingValue(s, idx, 0, out var end);
        end = SkipWhitespace(s, end);
        if (end != s.Length)
        {
            throw new WireJsonDecodeException("Extra data", end);
        }

        return value;
    }
}
