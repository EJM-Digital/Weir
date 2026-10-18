using System.Net;

namespace Weir.Contract.Tests.Harness;

/// <summary>What the harness takes for a ready server: only an OK answer that says so, and an answer that is not JSON is simply not ready.</summary>
[ContractArea("harness")]
public sealed class ReadyAnswerTests
{
    [Fact]
    public void An_ok_answer_that_says_ready_is_ready() =>
        Assert.Null(WeirServer.ReadyProblem(HttpStatusCode.OK, """{"ready":true}"""));

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"ready":false}""")]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"ready":true}""")]
    [InlineData(HttpStatusCode.OK, "{}")]
    public void An_answer_that_is_not_ready_says_what_it_was(HttpStatusCode status, string body) =>
        Assert.Equal($"HTTP {(int)status} {body}", WeirServer.ReadyProblem(status, body));

    [Theory]
    [InlineData("")]
    [InlineData("Starting")]
    [InlineData("<html></html>")]
    public void An_answer_that_is_not_JSON_is_not_ready_and_does_not_throw(string body) =>
        Assert.Equal($"HTTP 200 {body}", WeirServer.ReadyProblem(HttpStatusCode.OK, body));
}
