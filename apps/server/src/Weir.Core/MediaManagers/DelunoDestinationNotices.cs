namespace Weir.Core.MediaManagers;

/// <summary>The sentences for the ways Deluno's download destinations cannot be read, each saying what to do next.</summary>
internal static class DelunoDestinationNotices
{
    /// <summary>Where a Deluno API key is created and given its permissions.</summary>
    private const string KeysMenu = "System › API Access";

    public static string RouteMissing() =>
        $"Deluno {DelunoDestinationRules.FirstVersionOffering} or later lets Weir check where its download clients save and how its path mappings apply.";

    public static string NeedsImportsScope(string managerLabel) =>
        $"The API key Weir uses for {managerLabel} can't read where downloads go. Give it the Imports permission: " +
        $"in Deluno, open {KeysMenu} and create a key with Media automation access (it includes Imports), then save that key under Setup › Connections › Media managers in Weir.";

    public static string LibraryGone(string managerLabel) =>
        $"{LibraryGoneFact(managerLabel)}, so Weir can't check where its downloads go. {LibraryGoneAdvice(managerLabel)}";

    /// <summary>What happened when Deluno no longer lists the library a workflow came from (no closing full stop, so a sentence can follow).</summary>
    public static string LibraryGoneFact(string managerLabel) => $"{managerLabel} no longer has the library this workflow came from";

    /// <summary>
    /// Deluno answered that it has no library with the id the workflow is linked to. <paramref name="workflowName"/> is the
    /// workflow being checked, when the check is for a saved one.
    /// </summary>
    public static string LibraryRemoved(string managerLabel, string libraryName, string? workflowName)
    {
        var (workflow, that) = workflowName is { Length: > 0 } ? ($"the \"{workflowName}\" workflow", "that ") : ("this workflow", string.Empty);
        return $"{managerLabel} no longer has the {libraryName} library {that}{workflow} came from, so Weir can't check where its downloads go. " +
            $"Remove {workflow} if the library is gone for good, or unlink it from {managerLabel} to keep it as a Weir-only workflow.";
    }

    public static string LibraryGoneAdvice(string managerLabel) =>
        $"Remove this workflow if the library is gone for good, or unlink it from {managerLabel} to keep it as a Weir-only workflow.";

    /// <summary>The word for a media scope in these sentences: "TV" or "movie".</summary>
    public static string ScopeWord(string? mediaScope) => mediaScope == MediaManagerKinds.Tv ? "TV" : "movie";

    public static string StoppedRefining(string managerLabel, string libraryName, string? mediaScope) =>
        $"{managerLabel}'s {libraryName} library is no longer set to Refine before import, so {managerLabel} will not hand {ScopeWord(mediaScope)} downloads to Weir.";

    public static string StoppedRefiningAdvice(string managerLabel) =>
        $"Choose Refine before import for that library in {managerLabel}.";
}
