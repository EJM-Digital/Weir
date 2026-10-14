using Weir.Core.Json;

namespace Weir.Core.MediaManagers;

/// <summary>A report on a finished hand-off that Weir still owes a media manager: the hand-off's id and the file or release folder it was about.</summary>
public sealed record OwedReport(string HandoffId, string Name)
{
    public WireObject ToWire() => new WireObject().Set("handoff_id", HandoffId).Set("name", Name);
}

/// <summary>A workflow that still comes from a media manager connection.</summary>
public sealed record BlockedWorkflow(long Id, string Name)
{
    public WireObject ToWire() => new WireObject().Set("id", Id).Set("name", Name);
}

/// <summary>
/// What stops a media manager connection from being removed: the workflows that still come from it, the hand-offs from it
/// Weir is still working on, and the reports Weir still owes it. Removing a connection with any of these would leave a
/// workflow that was fed by a manager running as Weir's own, which scans its folder and may remove the originals a download
/// client is still seeding, so the removal is refused until the person has sorted them out.
/// </summary>
public sealed record ConnectionRemovalBlockers(IReadOnlyList<BlockedWorkflow> Workflows, int HandoffsInProgress, IReadOnlyList<OwedReport> Reports)
{
    /// <summary>The <c>code</c> of the 409 that refuses a removal.</summary>
    public const string InUseCode = "connection_in_use";

    /// <summary>The <c>code</c> of the 409 that refuses to unlink workflows while a hand-off from the connection is in progress.</summary>
    public const string HandoffsInProgressCode = "handoffs_in_progress";

    public static readonly ConnectionRemovalBlockers None = new([], 0, []);

    public bool Any => Workflows.Count > 0 || HandoffsInProgress > 0 || Reports.Count > 0;

    /// <summary>The members the refusal adds to the answer beside <c>detail</c> and <c>code</c>.</summary>
    public WireObject ToWire() => new WireObject()
        .Set("workflows", new WireArray(Workflows.Select(workflow => (WireValue)workflow.ToWire())))
        .Set("handoffs_in_progress", HandoffsInProgress)
        .Set("reports", new WireArray(Reports.Select(report => (WireValue)report.ToWire())));

    /// <summary>
    /// Why the connection <paramref name="label"/> was not removed and what to do first, e.g. "Movies and TV Shows still come
    /// from Deluno on nas. Nothing was removed. Before removing it, unlink or remove those workflows."
    /// </summary>
    public string RemovalRefusal(string label)
    {
        var name = label;
        string Subject()
        {
            var current = name;
            name = "it";
            return current;
        }

        var facts = new List<string>();
        var steps = new List<string>();
        if (Workflows.Count > 0)
        {
            var several = Workflows.Count > 1;
            facts.Add($"{NamesOf(Workflows)} {(several ? "still come" : "still comes")} from {Subject()}.");
            steps.Add(several ? "unlink or remove those workflows" : "unlink or remove that workflow");
        }

        if (HandoffsInProgress > 0)
        {
            facts.Add($"Weir is still working on {Counted(HandoffsInProgress, "hand-off")} from {Subject()}.");
            steps.Add(HandoffsInProgress == 1 ? "let the hand-off finish or cancel it" : "let the hand-offs finish or cancel them");
        }

        if (Reports.Count > 0)
        {
            facts.Add(Reports.Count == 1
                ? $"Weir still owes {Subject()} a report on a finished hand-off."
                : $"Weir still owes {Subject()} {Reports.Count} reports on finished hand-offs.");
            steps.Add(Reports.Count == 1 ? "let Weir deliver the report it owes or give up on it" : "let Weir deliver the reports it owes or give up on them");
        }

        return $"{string.Join(' ', facts)} Nothing was removed. Before removing it, {Joined(steps)}.";
    }

    /// <summary>Why the workflows of <paramref name="label"/> were not unlinked: a hand-off from it is still in progress.</summary>
    public string UnlinkRefusal(string label) =>
        $"Weir is still working on {Counted(HandoffsInProgress, "hand-off")} from {label}, so its workflows stay linked. Nothing was changed. " +
        (HandoffsInProgress == 1 ? "Let the hand-off finish or cancel it first." : "Let the hand-offs finish or cancel them first.");

    private static string Counted(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string NamesOf(IReadOnlyList<BlockedWorkflow> workflows) => Joined([.. workflows.Select(workflow => workflow.Name)]);

    /// <summary>"a", "a and b", "a, b and c".</summary>
    private static string Joined(List<string> items) =>
        items.Count <= 1 ? string.Concat(items) : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";
}
