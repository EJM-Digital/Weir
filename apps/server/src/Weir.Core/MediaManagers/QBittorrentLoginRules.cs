namespace Weir.Core.MediaManagers;

/// <summary>What qBittorrent's login answer means: whether it was accepted, and the session cookie to send on later calls.</summary>
public readonly record struct QBittorrentLogin(bool Accepted, string? SessionCookie)
{
    public static QBittorrentLogin Refused { get; } = new(false, null);
}

/// <summary>
/// Reads the answer to <c>POST /api/v2/auth/login</c> with no I/O. Older versions answer 200 with <c>Ok.</c> or
/// <c>Fails.</c> and name the session cookie <c>SID</c>. 5.x answers a good login with 204 and an empty body and
/// names the cookie <c>QBT_SID_&lt;port&gt;</c>, so the cookie is taken by whatever name it carries. A 204 with an
/// empty body is a good login with or without a cookie: a server answers it without one when it already trusts the
/// caller, for example when the login carried a live session (#842, #856). Any other empty answer needs a new
/// session cookie to count: a proxy or another program answering 200 with nothing is not qBittorrent (#870).
/// </summary>
public static class QBittorrentLoginRules
{
    private const string AcceptedBody = "Ok.";
    private const int NoContentStatus = 204;

    public static QBittorrentLogin Read(int status, string body, string? setCookie)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (status is < 200 or >= 300)
        {
            return QBittorrentLogin.Refused;
        }

        var cookie = SessionCookie.Leading(setCookie);
        var text = body.Trim();
        var accepted = text.Length == 0
            ? status == NoContentStatus || cookie is not null
            : string.Equals(text, AcceptedBody, StringComparison.Ordinal);
        return accepted ? new QBittorrentLogin(true, cookie) : QBittorrentLogin.Refused;
    }
}
