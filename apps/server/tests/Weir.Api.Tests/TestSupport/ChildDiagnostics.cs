using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Diagnostics.NETCore.Client;

namespace Weir.Api.Tests;

/// <summary>
/// What a test keeps of a child server that missed its ceiling, taken before the test stops it: a dump of the process and a text file with
/// its threads and loaded modules. The managed stack of every thread is in the dump (`dotnet-dump analyze`, `clrthreads`, `~* e !clrstack`): reading it
/// here would load the runtime's debugging library into the test host, and a child that is hung or shutting down can crash a reader. A server that prints nothing and never exits cannot be understood
/// afterwards from its exit code, and the failure may not come again. CI uploads the folder as an artifact when a run fails.
/// The child is only ever reached through the <see cref="Process"/> the test started, never by name.
/// </summary>
internal static class ChildDiagnostics
{
    /// <summary>The variable CI sets to the folder it uploads; without it the files go to a folder in the temp directory.</summary>
    internal const string FolderVariable = "WEIR_TEST_DIAGNOSTICS_DIR";

    internal static string Folder =>
        Environment.GetEnvironmentVariable(FolderVariable) is { Length: > 0 } set
            ? set
            : Path.Join(Path.GetTempPath(), "weir-test-diagnostics");

    /// <summary>Writes the dump and the report for <paramref name="process"/> and returns the path of the report; never throws, because the test is already failing.</summary>
    internal static string Capture(Process process, string label)
    {
        ArgumentNullException.ThrowIfNull(process);
        var name = string.Create(CultureInfo.InvariantCulture, $"{Safe(label)}-pid{process.Id}-{DateTime.UtcNow:yyyyMMddTHHmmssfff}");
        var report = new StringBuilder();
        var reportPath = Path.Join(Folder, name + ".txt");
        try
        {
            Directory.CreateDirectory(Folder);
            report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{label}: process {process.Id} had not finished at its ceiling."));
            Describe(process, report);
            Dump(process.Id, Path.Join(Folder, name + ".dmp"), report);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            report.AppendLine("Capturing failed: " + exception);
        }

        try
        {
            File.WriteAllText(reportPath, report.ToString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"(the report could not be written: {exception.Message})";
        }

        return reportPath;
    }

    private static void Describe(Process process, StringBuilder report)
    {
        try
        {
            process.Refresh();
            report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"cpu={process.TotalProcessorTime} threads={process.Threads.Count} handles={process.HandleCount}"));
            foreach (ProcessThread thread in process.Threads)
            {
                var wait = thread.ThreadState == System.Diagnostics.ThreadState.Wait ? thread.WaitReason.ToString() : string.Empty;
                report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  os thread {thread.Id} {thread.ThreadState} {wait}"));
            }

            foreach (ProcessModule module in process.Modules)
            {
                report.AppendLine("  module " + module.FileName);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            report.AppendLine("The process could not be described: " + exception.Message);
        }
    }

    private static void Dump(int processId, string path, StringBuilder report)
    {
        try
        {
            new DiagnosticsClient(processId).WriteDump(DumpType.WithHeap, path);
            report.AppendLine("dump: " + path);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            report.AppendLine("The dump could not be written: " + exception.Message);
        }
    }

    private static string Safe(string text) => string.Concat(text.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')).TrimEnd('_');
}
