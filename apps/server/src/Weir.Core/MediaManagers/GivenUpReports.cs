using Weir.Core.Json;

namespace Weir.Core.MediaManagers;

/// <summary>What Activity says when a person told Weir to stop owing a media manager reports on finished hand-offs.</summary>
public static class GivenUpReports
{
    public static string Title(string label, int count) =>
        count == 1 ? $"Weir gave up on a report it owed {label}" : $"Weir gave up on {count} reports it owed {label}";

    /// <summary>The Activity detail: the reports dropped, so the record says what Weir will never tell the manager.</summary>
    public static string Detail(string label, IReadOnlyList<OwedReport> reports) =>
        WireJsonWriter.Dumps(
            new WireObject()
                .Set("trigger", "manual")
                .Set("result", "success")
                .Set("reports", new WireArray(reports.Select(report => (WireValue)report.ToWire())))
                .Set("message", $"Weir will not tell {label} about {(reports.Count == 1 ? "this finished hand-off" : "these finished hand-offs")}: {string.Join(", ", reports.Select(report => report.Name))}."),
            WireJsonFormat.Compact);
}
