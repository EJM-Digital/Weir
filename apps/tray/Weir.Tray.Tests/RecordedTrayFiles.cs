namespace Weir.Tray.Tests;

/// <summary>
/// The files the tray and the server write for each other, as each really writes them (apps/server/tests/fixtures/tray-files). The
/// server's tests read the same files, so a key renamed or a format changed on either side fails a test on that side.
/// </summary>
internal static class RecordedTrayFiles
{
    /// <summary>The moment every recorded file was written at.</summary>
    internal static readonly DateTimeOffset WrittenAt = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    internal static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TrayFiles", name));

    /// <summary>Puts the recorded file in <paramref name="runtimeHome"/> under the name the reader looks for.</summary>
    internal static void PlaceIn(string runtimeHome, string name, string? fileName = null) =>
        File.WriteAllText(Path.Combine(runtimeHome, fileName ?? name), Text(name));

    /// <summary>The text with its line endings made the same, since a checkout may differ from what Windows wrote.</summary>
    internal static string Normalised(string text) => text.ReplaceLineEndings("\n");
}
