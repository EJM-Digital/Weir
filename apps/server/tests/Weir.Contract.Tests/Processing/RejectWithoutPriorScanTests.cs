using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// A rejected hand-off must have a Files row even when no watched-folder scan ever saw the file. A hand-off Weir has never scanned
/// has no <c>files</c> row yet, so a rejection that only updated an existing row would be dropped everywhere except Activity.
/// Reading it back also relies on <c>GET /processing/files</c> listing <c>rejected</c> rows (#530).
/// </summary>
[ContractArea("processing")]
public sealed class RejectWithoutPriorScanTests
{
    [Fact]
    public async Task A_rejection_with_no_prior_scan_still_creates_a_files_row()
    {
        await using var scenario = await Scenario.StartAsync();
        var (_, library) = await scenario.DelunoSetupAsync(
            capabilities: ["processor-reject-regrab"], library: [("failure_policy", "reject")]);
        // A video with no audio at all: content the reject policy blocklists outright.
        var source = scenario.WriteRelease("No.Prior.Scan.532", "film.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: [])));
        const string rel = "No.Prior.Scan.532/film.mkv";

        // No DetectWithoutQueueingAsync, no scan: nothing but the hand-off itself has ever looked
        // at this file. That is the exact scenario the issue describes.
        await scenario.PostHandoffAsync("handoff-noscan-532", source);
        await scenario.WaitForHandoffStateAsync("handoff-noscan-532", "rejected");

        var row = await scenario.WaitForFileStatusAsync(library, rel, "rejected");
        Assert.Contains("no retainable audio", (string)row["status_reason"]!, StringComparison.Ordinal);
    }
}
