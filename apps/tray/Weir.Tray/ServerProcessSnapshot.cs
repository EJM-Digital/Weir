using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Weir.Tray;

/// <summary>
/// A sentence about a server process that has not answered, for tray-host.log: how long it has run, how much processor time it has
/// used, how many threads and how much memory it has, and which files outside Windows it has loaded. A server that never got
/// going has used none and has loaded nothing of its own; one that is busy or blocked has loaded the libraries it works with. That
/// is the difference between "never started" and "started and stuck" when there is nothing else to go on.
/// </summary>
static class ServerProcessSnapshot
{
    private const int MostFilesNamed = 12;

    internal static string Describe(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return "had already exited";
            }

            var ran = DateTime.UtcNow - process.StartTime.ToUniversalTime();
            var files = LoadedFiles(process);
            var named = string.Join(", ", files.Take(MostFilesNamed));
            var more = files.Count > MostFilesNamed ? $" and {files.Count - MostFilesNamed} more" : string.Empty;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"it had run for {ran.TotalSeconds:0.#} s, used {process.TotalProcessorTime.TotalSeconds:0.##} s of processor time, held {process.Threads.Count} threads and {process.WorkingSet64 / (1024 * 1024)} MB, and had loaded {files.Count} files outside Windows: {named}{more}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return $"could not be looked at ({ex.Message})";
        }
    }

    // Only files outside the Windows folder: the server's own, the .NET runtime's and what it extracted.
    private static List<string> LoadedFiles(Process process)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var files = new List<string>();
        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                if (!module.FileName.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(Path.GetFileName(module.FileName));
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // A process that is starting or stopping cannot list its modules; the rest of the sentence still stands.
        }

        return files;
    }
}
