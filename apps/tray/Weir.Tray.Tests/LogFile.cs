using System.Text;

namespace Weir.Tray.Tests;

/// <summary>
/// Reads a file the tray is still writing. A log reader must not exclude the writer, nor be excluded by it: the tray logs from
/// background threads, so a line may be on its way into tray-host.log at the moment a test reads it.
/// </summary>
internal static class LogFile
{
    internal static string ReadAllText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static string[] ReadAllLines(string path)
    {
        using var reader = new StringReader(ReadAllText(path));
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }
        return [.. lines];
    }
}
