namespace Weir.Tray;

/// <summary>
/// What "Report a problem..." opens. While the repositories are private that is the logs folder, where the person finds
/// what to attach. Once they are public it becomes the new-issue page on GitHub with the version filled in; the link
/// then carries the version and nothing else, never a path or an address. That switch is this method.
/// </summary>
static class ProblemReport
{
    internal static void Open(ITrayShell shell, string logsFolder) => shell.OpenFolder(logsFolder, "logs folder");
}
