using Weir.Core.Json;

namespace Weir.Core.MediaManagers;

/// <summary>
/// Deluge Web UI JSON-RPC answers, read with no I/O. <c>core.get_config</c> gives the folder completed downloads end up in:
/// <c>move_completed_path</c> when <c>move_completed</c> is on, and <c>download_location</c> when it is off, because then they stay
/// where they were downloaded. <c>label.get_labels</c> lists the label names and <c>label.get_options</c> one label's options, which put
/// a download in its own folder only when <c>apply_move_completed</c> and <c>move_completed</c> are both on. A JSON-RPC <c>error</c>
/// answer (the label plugin not being enabled, most often) means no labels, not a failed read.
/// </summary>
public static class DelugeRules
{
    public static DownloadClientFolders Parse(WireValue? coreConfigResponse, IReadOnlyList<(string Label, WireValue? Options)> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var folders = new List<DownloadClientCategoryFolder>();
        foreach (var (label, options) in labels)
        {
            if (LabelFolder(options) is { } folder)
            {
                folders.Add(new DownloadClientCategoryFolder(label, folder));
            }
        }

        return new DownloadClientFolders(CompletedFolder(coreConfigResponse), [.. folders.OrderBy(folder => folder.Category, StringComparer.Ordinal)]);
    }

    /// <summary>The label names in a <c>label.get_labels</c> answer; none when it answered an error.</summary>
    public static IReadOnlyList<string> LabelNames(WireValue? labelsResponse) =>
        labelsResponse is WireObject dict && IsSuccess(dict) && dict.Get("result") is WireArray names
            ? [.. names.Items.Select(ManagerValues.Text).OfType<string>()]
            : [];

    private static string? CompletedFolder(WireValue? coreConfigResponse)
    {
        if (RpcResult(coreConfigResponse) is not { } core)
        {
            return null;
        }

        return core.Get("move_completed") is WireBool { Value: true }
            ? ManagerValues.Text(core.Get("move_completed_path"))
            : ManagerValues.Text(core.Get("download_location"));
    }

    private static string? LabelFolder(WireValue? optionsResponse) =>
        RpcResult(optionsResponse) is { } options && options.Get("apply_move_completed") is WireBool { Value: true } && options.Get("move_completed") is WireBool { Value: true }
            ? ManagerValues.Text(options.Get("move_completed_path"))
            : null;

    /// <summary>A JSON-RPC <c>result</c> object, or null when the call answered an <c>error</c> instead.</summary>
    private static WireObject? RpcResult(WireValue? response) =>
        response is WireObject dict && IsSuccess(dict) && dict.Get("result") is WireObject result ? result : null;

    private static bool IsSuccess(WireObject response) => response.Get("error") is null or WireNull;
}
