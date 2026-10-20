using Microsoft.Extensions.Primitives;
using Weir.Api.Http;

namespace Weir.Api.Tests.Http;

/// <summary>Cookie parsing and the <c>Set-Cookie</c> header, whose exact bytes the web app and the contract suite read.</summary>
public sealed class CookieHeadersTests
{
    [Fact]
    public void Parse_reads_each_header_and_each_pair_and_the_last_value_for_a_name_wins()
    {
        var cookies = CookieHeaders.Parse(new StringValues(["a=1; b=2", "a=3"]));

        Assert.Equal(new Dictionary<string, string> { ["a"] = "3", ["b"] = "2" }, cookies);
    }

    [Fact]
    public void Parse_trims_names_and_values_and_unquotes_quoted_values()
    {
        var cookies = CookieHeaders.Parse(new StringValues("  name = \"two words\" ;other=plain"));

        Assert.Equal("two words", cookies["name"]);
        Assert.Equal("plain", cookies["other"]);
    }

    [Fact]
    public void Parse_keeps_a_pair_with_no_equals_sign_under_an_empty_name_and_skips_empty_pairs()
    {
        var cookies = CookieHeaders.Parse(new StringValues("lonely; ; =; x=1"));

        Assert.Equal(new Dictionary<string, string> { [string.Empty] = "lonely", ["x"] = "1" }, cookies);
    }

    [Fact]
    public void Parse_of_no_header_is_empty()
    {
        Assert.Empty(CookieHeaders.Parse(StringValues.Empty));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("\"x\"", "x")]
    [InlineData("\"", "\"")]
    [InlineData("\"a\\\\b\"", "a\\b")]
    [InlineData("\"say \\\"hi\\\"\"", "say \"hi\"")]
    [InlineData("\"semi\\073colon\"", "semi;colon")]
    [InlineData("\"\\1\"", "1")]
    [InlineData("\"end\\\"", "end\\")]
    [InlineData("\"\\089\"", "089")]
    public void Unquote_strips_quotes_and_decodes_backslash_and_octal_escapes(string raw, string expected)
    {
        Assert.Equal(expected, CookieHeaders.Unquote(raw));
    }

    [Fact]
    public void A_cookie_value_with_any_character_outside_the_legal_set_survives_a_round_trip()
    {
        const string value = "a b,c;d\"e\\f(g)\u00e9\t~";

        var header = CookieHeaders.SetCookieHeader("session", value, maxAge: null, expires: null, httpOnly: false, secure: false, sameSite: "Lax");
        var parsed = CookieHeaders.Parse(new StringValues(header[..header.IndexOf("; Path=", StringComparison.Ordinal)]));

        Assert.Equal(value, parsed["session"]);
    }

    [Fact]
    public void An_empty_cookie_value_is_written_as_two_quotes_and_a_legal_one_as_it_is()
    {
        Assert.Equal("t=\"\"; Path=/; SameSite=Strict", CookieHeaders.SetCookieHeader("t", string.Empty, null, null, false, false, "Strict"));
        Assert.Equal("t=abc-1.2_3; Path=/; SameSite=Strict", CookieHeaders.SetCookieHeader("t", "abc-1.2_3", null, null, false, false, "Strict"));
    }

    [Fact]
    public void Set_cookie_attributes_come_in_the_fixed_order_existing_clients_expect()
    {
        var header = CookieHeaders.SetCookieHeader(
            "weir", "v", maxAge: 3600, expires: new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero), httpOnly: true, secure: true, sameSite: "Lax", path: "/api");

        Assert.Equal("weir=v; expires=Wed, 04 Mar 2026 05:06:07 GMT; HttpOnly; Max-Age=3600; Path=/api; SameSite=Lax; Secure", header);
    }

    [Fact]
    public void A_cookie_with_no_lifetime_flags_or_security_has_only_a_path_and_a_same_site_rule()
    {
        Assert.Equal("weir=v; Path=/; SameSite=None", CookieHeaders.SetCookieHeader("weir", "v", null, null, false, false, "None"));
    }
}
