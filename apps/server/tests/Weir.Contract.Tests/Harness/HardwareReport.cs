using System.Net;

namespace Weir.Contract.Tests.Harness;

/// <summary>The hardware report of a server whose ffmpeg is a tool of the harness's.</summary>
internal static class HardwareReport
{
    /// <summary>
    /// The report once ffmpeg has answered it within the 10 seconds the check allows. How long a tool takes to start depends on the machine,
    /// and a check that ran out of time says so and is run again by the next request, so a test that needs the answer asks again.
    /// </summary>
    public static Task<WeirResponse> OnceFfmpegAnswersAsync(WeirClient admin) =>
        Poll.UntilAsync(
            async () =>
            {
                var response = await admin.GetAsync($"{WeirClient.Api}/processing/hardware");
                Assert.Equal(HttpStatusCode.OK, response.Status);
                return (bool)response.Fields["detected"]! ? response : null;
            },
            "ffmpeg to answer the hardware check in time");
}
