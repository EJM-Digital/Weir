using Weir.Core.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Core.Tests.MediaManagers;

/// <summary><see cref="SessionCookie"/> on the <c>Set-Cookie</c> headers real download clients sent (<c>fixtures/recorded</c>).</summary>
public sealed class SessionCookieTests
{
    public static TheoryData<string, string, string> Recorded => new()
    {
        { "deluge", "test", "_session_id=<scrubbed>" },
        { "qbittorrent", "test", "SID=<scrubbed>" },
    };

    [Theory]
    [MemberData(nameof(Recorded))]
    public void The_cookie_is_its_name_and_value_without_the_attributes_the_client_set(string app, string state, string expected)
    {
        var setCookie = ArrRecording.Load(app, state).Exchanges[0].Reply.Headers["set-cookie"];

        Assert.Contains(';', setCookie);
        Assert.Equal(expected, SessionCookie.Leading(setCookie));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("HttpOnly")]
    [InlineData("SID=")]
    [InlineData("=value")]
    public void A_header_without_a_name_and_a_value_is_no_cookie(string? setCookie)
    {
        Assert.Null(SessionCookie.Leading(setCookie));
    }
}
