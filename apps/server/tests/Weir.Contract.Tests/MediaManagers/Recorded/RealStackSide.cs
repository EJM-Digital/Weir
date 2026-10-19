using Weir.Contract.Tests.Harness.Fakes;
using Weir.Contract.Tests.Processing;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// What the recordings made in Compose have in common (<c>fixtures/recorded/deluno-side/&lt;version&gt;/real-*.json</c>): a real Deluno and a real Weir in
/// containers, with a recording proxy between them that wrote down every call. A recording is the conversation about one hand-off, in order,
/// or the exchanges of connecting the two. Paths are a container's: <c>/downloads/completed/Movies</c> is where Deluno's client left a release,
/// <c>/weir/Ready/Movies</c> where Weir hands cleaned files back.
/// </summary>
internal static class RealStackSide
{
    public const string HandoffPath = "/api/v1/intake/webhook/deluno";

    public static Recording Load(string state) => Recording.LoadDelunoSide(Recording.DelunoSideRealStackVersion, state);

    /// <summary>The world the recordings were made in: Deluno's Movies library by the id the hand-off names, handing back to Deluno's folder.</summary>
    public static Task<RecordedWorld> StartWorldAsync(Recording recording, params (string Name, string Value)[] settings) =>
        RecordedWorld.StartAsync(LibraryIdOf(recording), RecordedWorld.ContainerMoviesOutput, settings: settings);

    /// <summary>The world the TV recordings were made in: Deluno's TV library by the id the hand-off names, handing back to Deluno's folder.</summary>
    public static Task<RecordedWorld> StartTvWorldAsync(Recording recording, params (string Name, string Value)[] settings) =>
        RecordedWorld.StartAsync(tvKey: LibraryIdOf(recording), delunoOutputRoot: RecordedWorld.ContainerMoviesOutput, settings: settings);

    public static string LibraryIdOf(Recording recording) => (string)HandoffOf(recording).Request.Body!["libraryId"]!;

    /// <summary>The exchange in which Deluno handed the release to Weir.</summary>
    public static RecordedExchange HandoffOf(Recording recording) => Only(recording, exchange => exchange.Request.Path == HandoffPath);

    /// <summary>The position in the recording of the exchange <paramref name="of"/> picks; the one that is not there fails with what the recording holds.</summary>
    public static int IndexOf(Recording recording, Func<RecordedExchange, bool> of, string what) =>
        recording.Exchanges.ToList().FindIndex(exchange => of(exchange)) is var index and >= 0
            ? index
            : throw new Xunit.Sdk.XunitException($"{recording.File} holds no {what}; it holds {string.Join(", ", recording.Exchanges.Select(exchange => $"{exchange.Request.Method} {exchange.Request.Path}"))}.");

    public static RecordedExchange Only(Recording recording, Func<RecordedExchange, bool> of) => recording.Exchanges[IndexOf(recording, of, "such exchange")];

    /// <summary>The position of Weir's report of this status to Deluno.</summary>
    public static int ReportIndex(Recording recording, string status) =>
        IndexOf(recording, exchange => exchange.Request.Path == Scenario.EventsPath && (string?)exchange.Request.Body?["status"] == status, $"{status} report");

    /// <summary>The position of Deluno's outcome for the hand-off.</summary>
    public static int OutcomeIndex(Recording recording) => IndexOf(recording, exchange => exchange.Request.Path.EndsWith("/outcome", StringComparison.Ordinal), "outcome");

    /// <summary>The files the hand-off names, as Deluno sent them.</summary>
    public static List<string> SourceFilesOf(RecordedExchange handoff) => [.. handoff.Request.Body!["sourceFiles"]!.AsArray().Select(file => (string)file!)];

    /// <summary>Makes the fake Deluno answer Weir's report as the real one answered it.</summary>
    public static void AnswerReportAs(this FakeManager deluno, RecordedExchange report) =>
        deluno.Route("POST", Scenario.EventsPath, _ => DelunoSide.AnswerOf(report.Reply));
}
