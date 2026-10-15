using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.Contract.Tests.Processing;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// What the recordings of Deluno's own side (<c>fixtures/recorded/deluno-side/</c>) have in common: where they were recorded, the ids
/// they name, and how to ask for a hand-off the recordings do not hold with the values that differ and nothing else.
/// </summary>
internal static class DelunoSide
{
    /// <summary>Deluno's id for its Movies library in the session the rc.30.30 recordings were made in.</summary>
    public const string MoviesKey = "01a136e9242275898618c522d88dec53";

    /// <summary>Deluno's id for its Movies library in the session the rc.30.32 reports (the retries, the takeover) were recorded in.</summary>
    public const string ReportsMoviesKey = "01a13d68ae4a7e7b84c06a96fd5d8cc7";

    /// <summary>Deluno's id for its TV library in that session.</summary>
    public const string TvKey = "01a136e9255c79f9b93c4e6b81736481";

    /// <summary>Where Deluno says Weir hands its cleaned files back to, written as Deluno's machine writes it.</summary>
    public const string DelunoReadyFolder = RecordedWorld.RecordedMoviesOutput;

    /// <summary>The single-file hand-off recording the nearest-recorded requests are made from.</summary>
    private const string SingleFileHandoff = "handoff-single-file";

    /// <summary>A recording of what Deluno sent Weir, or Weir sent Deluno, in the rc.30.30 session.</summary>
    public static Recording Request(string state) => Recording.LoadDelunoSide(Recording.DelunoSideRequestsVersion, state);

    /// <summary>A recording of Deluno's own reply to a call Weir makes, from the rc.30.32 session.</summary>
    public static Recording Reply(string state) => Recording.LoadDelunoSide(Recording.DelunoSideRepliesVersion, state);

    /// <summary>A recording of a report Weir sent Deluno, with Deluno's answer to it, from the rc.30.32 session.</summary>
    public static Recording Report(string state) => Recording.LoadDelunoSide(Recording.DelunoSideRepliesVersion, state);

    /// <summary>The world of the rc.30.32 report recordings: Deluno's Movies library by the id of that session, handing back to Deluno's recorded folder.</summary>
    public static Task<RecordedWorld> StartReportsWorldAsync(params (string Name, string Value)[] settings) =>
        RecordedWorld.StartAsync(ReportsMoviesKey, DelunoReadyFolder, settings);

    /// <summary>The world of the rc.30.30 recordings: Deluno's Movies library by its recorded id, handing back to Deluno's recorded folder.</summary>
    public static Task<RecordedWorld> StartWorldAsync(params (string Name, string Value)[] settings) =>
        RecordedWorld.StartAsync(MoviesKey, DelunoReadyFolder, settings);

    /// <summary>
    /// A hand-off for a download the recordings do not hold a hand-off of (a status poll or a report they do hold names its id and
    /// release): the recorded single-file hand-off with that id and release name, and nothing else changed, with the Idempotency-Key
    /// carrying the new id as Deluno builds it.
    /// </summary>
    public static RecordedCall HandoffLike(string handoffId, string releaseName, string? fileName = null)
    {
        var recorded = Request(SingleFileHandoff).Only.Request;
        var body = recorded.Body!.DeepClone().AsObject();
        body["handoffId"] = handoffId;
        body["releaseName"] = releaseName;
        body["sourcePath"] = $@"C:\Downloads\Completed\Movies\{fileName ?? releaseName}";
        var headers = recorded.Headers.ToDictionary(header => header.Key, header => header.Value);
        headers["idempotency-key"] = handoffId + headers["idempotency-key"][headers["idempotency-key"].IndexOf(':')..];
        return recorded with { Body = body, Headers = headers };
    }

    /// <summary>
    /// The hand-off Deluno made for the release a report recording names (its id, release name and library): the recorded single-file hand-off
    /// for <paramref name="fileName"/>, or for the file the report's output has the name of, and nothing else changed.
    /// </summary>
    public static RecordedCall HandoffFor(RecordedExchange report, string? fileName = null)
    {
        var sent = (JsonObject)report.Request.Body!;
        var hand = HandoffLike((string)sent["handoffId"]!, (string)sent["releaseName"]!, fileName ?? FileNameOf((string)sent["outputPath"]!));
        hand.Body!["libraryId"] = (string)sent["libraryId"]!;
        return hand;
    }

    /// <summary>The name of the file at the end of a path as Deluno's Windows machine writes it.</summary>
    public static string FileNameOf(string windowsPath) => windowsPath[(windowsPath.LastIndexOf('\\') + 1)..];

    /// <summary>What a fake Deluno answers: the recorded status, and the recorded JSON or text.</summary>
    public static Reply AnswerOf(RecordedAnswer recorded) => new(recorded.Status, recorded.Body?.DeepClone(), Text: recorded.BodyText);

    /// <summary>The id a status poll, cancel or outcome names in its path: the segment after <c>/handoffs/deluno/</c>.</summary>
    public static string HandoffIdInPath(this RecordedCall call)
    {
        const string marker = "/handoffs/deluno/";
        var start = call.Path.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = call.Path.IndexOf('/', start);
        return end < 0 ? call.Path[start..] : call.Path[start..end];
    }

    public static string HandoffId(this RecordedCall hand) => (string)hand.Body!["handoffId"]!;

    public static string SourcePath(this RecordedCall hand) => (string)hand.Body!["sourcePath"]!;

    /// <summary>The release name a report recording gives a hand-off, which is the file's name for a single file.</summary>
    public static string ReleaseNameOf(RecordedExchange report) => (string)report.Request.Body!["releaseName"]!;

    /// <summary>The recorded hand-off request with <paramref name="handoffId"/>, from any of the hand-off recordings, if one holds it.</summary>
    public static RecordedCall? RecordedHandoff(string handoffId) =>
        new[] { "handoff-single-file", "handoff-release-folder", "handoff-usenet-folder", "handoff-resend" }
            .SelectMany(state => Request(state).Exchanges)
            .Select(exchange => exchange.Request)
            .FirstOrDefault(request => request.HandoffId() == handoffId);

    /// <summary>
    /// Makes the fake Deluno answer each report Weir sends as the real Deluno answered the report of the same hand-off and status in
    /// <paramref name="recordings"/>, and a report no recording holds with a 500 that names it, so a report the recordings did not expect fails loudly.
    /// </summary>
    public static void AnswerReportsAsRecorded(this FakeManager deluno, params Recording[] recordings) => deluno.AnswerReportsAsRecorded(null, recordings);

    /// <summary>The same, for recordings whose ids the test has made its own with <paramref name="translation"/>.</summary>
    public static void AnswerReportsAsRecorded(this FakeManager deluno, Translation? translation, params Recording[] recordings)
    {
        var answers = recordings.SelectMany(recording => recording.Exchanges).ToList();
        string Live(string recordedId) => translation?.Id(recordedId) ?? recordedId;
        deluno.Route("POST", Scenario.EventsPath, request =>
        {
            var report = (JsonObject)request.Json!;
            var answer = answers.FirstOrDefault(exchange =>
                Live((string)exchange.Request.Body!["handoffId"]!) == (string)report["handoffId"]! && (string)exchange.Request.Body["status"]! == (string)report["status"]!);
            return answer is null
                ? new Reply(500, new JsonObject { ["message"] = $"no recording answers a {report["status"]} report of {report["handoffId"]}" })
                : AnswerOf(answer.Reply);
        });
    }
}
