using Microsoft.Playwright;

namespace Weir.E2E.Tests.Support;

/// <summary>A timestamp from the server as the screens write it: the medium date and short time, in the time zone chosen in System › About.</summary>
public static class AppDates
{
    /// <summary>
    /// What a screen shows for <paramref name="serverTimestamp"/> (the server writes UTC without a zone, so one without a zone is read as
    /// UTC) in <paramref name="timeZone"/>, formatted by the browser the page runs in.
    /// </summary>
    public static Task<string> ShownAsync(IPage page, string serverTimestamp, string timeZone) =>
        page.EvaluateAsync<string>(
            """
            ({ serverTimestamp, timeZone }) => {
              const zoned = /(?:[zZ]|[+-]\d\d:?\d\d)$/.test(serverTimestamp) ? serverTimestamp : serverTimestamp + 'Z';
              return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short', timeZone }).format(new Date(zoned));
            }
            """,
            new { serverTimestamp, timeZone });
}
