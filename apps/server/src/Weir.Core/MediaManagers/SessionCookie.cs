namespace Weir.Core.MediaManagers;

/// <summary>The session cookie a download client set, in the form a client sends it back.</summary>
public static class SessionCookie
{
    /// <summary>The leading <c>name=value</c> of a <c>Set-Cookie</c> header, without its attributes (<c>Path</c>, <c>Expires</c>, <c>HttpOnly</c>), or null when there is none.</summary>
    public static string? Leading(string? setCookie)
    {
        var pair = setCookie?.Split(';', 2)[0].Trim();
        if (pair is null)
        {
            return null;
        }

        var separator = pair.IndexOf('=', StringComparison.Ordinal);
        return separator > 0 && separator < pair.Length - 1 ? pair : null;
    }
}
