using System.Net;

namespace Weir.Contract.Tests.Harness;

/// <summary>The hardware report of a server whose ffmpeg is a tool of the harness's.</summary>
internal static class HardwareReport
{
    /// <summary>
    /// How long a test keeps asking. The server stops each check after 15 seconds; ffmpeg runs at normal priority, so a check is
    /// answered in time on any machine that can run the tests at all. The wait ends when the first check is answered in time, so it
    /// is one request unless the first start of the tool is slow, as it can be on a cold runner.
    /// </summary>
    private static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The report once ffmpeg has answered it within the check's 15 seconds. A check that ran out of time says so, and the next request
    /// runs it afresh, so a test that needs the answer asks again.
    /// </summary>
    public static Task<WeirResponse> OnceFfmpegAnswersAsync(WeirClient admin) =>
        Poll.UntilAsync(
            async () =>
            {
                var response = await admin.GetAsync($"{WeirClient.Api}/processing/hardware");
                Assert.Equal(HttpStatusCode.OK, response.Status);
                Assert.True((bool)response.Fields["detected"]!, (string?)response.Fields["detail"]);
                return response;
            },
            "ffmpeg to answer the hardware check in time",
            Ceiling);
}
