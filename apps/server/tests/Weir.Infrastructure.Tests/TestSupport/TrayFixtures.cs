namespace Weir.Infrastructure.Tests.Platform;

/// <summary>
/// The files the Windows tray and the server write for each other, as each really writes them (fixtures/tray-files/README.md). The
/// tray's tests read the same files with the tray's own readers.
/// </summary>
internal static class TrayFixtures
{
    internal static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "tray-files", name));

    /// <summary>Puts the recorded file in <paramref name="folder"/> under <paramref name="fileName"/>, the name the server looks for.</summary>
    internal static void PlaceIn(string folder, string name, string fileName) => File.WriteAllText(Path.Join(folder, fileName), Text(name));
}
