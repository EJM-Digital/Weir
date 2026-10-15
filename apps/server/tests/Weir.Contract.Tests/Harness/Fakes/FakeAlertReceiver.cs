using System.Globalization;
using System.Net;

namespace Weir.Contract.Tests.Harness.Fakes;

/// <summary>
/// The far end of an alert: a webhook or Discord address played by a local HTTP server that records every post it receives and answers
/// each address with the status a test set for it (204 otherwise). A server under test sends every alert here when it is started with
/// <see cref="Env"/>; the address a channel names is a public-looking name that the server holds to the same rules as for real
/// (<c>WEIR_NOTIFICATION_STAND_IN_PORT</c> moves only the connection).
/// <code>
/// using var receiver = new FakeAlertReceiver();
/// await using var server = await WeirServer.StartNewAsync(receiver.Env);
/// // a channel whose address is http://hooks.example.com/alerts/one reaches the receiver at the path /alerts/one
/// </code>
/// </summary>
public sealed class FakeAlertReceiver : FakeHttpServer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (int Status, string? Location)> _answers = [];

    /// <summary>What a server needs to send its alerts here: pass it to <see cref="WeirServer.StartNewAsync"/>.</summary>
    public IReadOnlyDictionary<string, string> Env => new Dictionary<string, string>
    {
        ["WEIR_ENV"] = "development",
        ["WEIR_NOTIFICATION_STAND_IN_PORT"] = new Uri(BaseUrl).Port.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Answers every following post to <paramref name="path"/> with this status.</summary>
    public void Answers(string path, HttpStatusCode status)
    {
        lock (_gate)
        {
            _answers[path] = ((int)status, null);
        }
    }

    /// <summary>Answers every following post to <paramref name="path"/> with a 302 to <paramref name="location"/>.</summary>
    public void RedirectsTo(string path, string location)
    {
        lock (_gate)
        {
            _answers[path] = ((int)HttpStatusCode.Found, location);
        }
    }

    /// <summary>Waits until <paramref name="count"/> posts have arrived at <paramref name="path"/> and returns them; fails with what did arrive after the timeout.</summary>
    public Task<IReadOnlyList<RecordedRequest>> WaitForPostsAsync(string path, int count, TimeSpan? timeout = null) =>
        WaitForAsync(request => request.Path == path, count, timeout, $"an alert at {path}");

    /// <summary>The posts that have arrived at <paramref name="path"/>, oldest first.</summary>
    public IReadOnlyList<RecordedRequest> PostsTo(string path) => [.. Requests.Where(request => request.Path == path)];

    protected override HttpAnswer Answer(RecordedRequest request)
    {
        (int Status, string? Location) answer;
        lock (_gate)
        {
            answer = _answers.GetValueOrDefault(request.Path, ((int)HttpStatusCode.NoContent, null));
        }

        return new HttpAnswer(answer.Status, [], Headers: answer.Location is null ? null : new Dictionary<string, string> { ["Location"] = answer.Location });
    }
}
