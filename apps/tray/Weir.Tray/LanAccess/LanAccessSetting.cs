namespace Weir.Tray.LanAccess;

/// <summary>
/// The saved "LAN access" choice in the runtime home: whether the server listens for other devices on the network
/// or only for this PC. The tray reads it at every start, and <c>--allow-lan</c> writes it from another process.
/// </summary>
static class LanAccessSetting
{
    internal const string FileName = "lan-access";

    private const string OtherDevicesText = "on";
    private const string ThisPcOnlyText = "off";

    /// <summary>What a look at the saved choice found.</summary>
    /// <param name="Busy">The file could not be read after several looks (another process was replacing it). That says nothing about the choice.</param>
    /// <param name="Scope">The choice, or null when none is saved. Text that is not understood reads as <see cref="ListenScope.ThisPcOnly"/>.</param>
    /// <param name="SavedAt">When the choice was last written, or null when nothing is saved.</param>
    internal readonly record struct Saved(bool Busy, ListenScope? Scope, DateTime? SavedAt);

    /// <summary>
    /// Looks at the saved choice once, the file and its time together. Another process (the server's page, <c>--allow-lan</c>) replaces
    /// the file by renaming a new one over it, so a refusal or a missing name is looked at again (<see cref="ReplacedFile"/>) before the
    /// file is called busy or not saved. A caller that is changing something because of what it reads must treat
    /// <see cref="Saved.Busy"/> as "not yet", never as a choice.
    /// </summary>
    internal static Saved Look(string runtimeHome, Action<string> log)
    {
        var reading = ReplacedFile.ReadText(Path.Combine(runtimeHome, FileName));
        switch (reading.State)
        {
            case ReplacedFileState.Absent:
                return new Saved(false, null, null);
            case ReplacedFileState.Busy:
                log($"{FileName} could not be read just now (it was being replaced or scanned).");
                return new Saved(true, null, null);
        }

        var text = reading.Text.Trim();
        if (text is OtherDevicesText)
        {
            return new Saved(false, ListenScope.OtherDevices, reading.WrittenUtc);
        }
        if (text is not ThisPcOnlyText)
        {
            log($"{FileName} says '{text}', which Weir does not recognise; treating LAN access as off.");
        }
        return new Saved(false, ListenScope.ThisPcOnly, reading.WrittenUtc);
    }

    /// <summary>
    /// The saved choice, or null when nothing has been saved yet. A file that cannot be read, even after several looks, or understood
    /// reads as <see cref="ListenScope.ThisPcOnly"/>, and <paramref name="log"/> says why: opening Weir to the network
    /// is never a guess. For the start, which must choose; the watcher uses <see cref="Look"/> and waits instead.
    /// </summary>
    internal static ListenScope? Read(string runtimeHome, Action<string> log)
    {
        var saved = Look(runtimeHome, log);
        return saved.Busy ? ListenScope.ThisPcOnly : saved.Scope;
    }

    /// <summary>
    /// When the choice was last written, or null when nothing is saved or it could not be read. Saving the same choice again moves it,
    /// which is how a person asking again is told apart from a choice that was already there.
    /// </summary>
    internal static DateTime? SavedAt(string runtimeHome) => Look(runtimeHome, _ => { }).SavedAt;

    internal static void Write(string runtimeHome, ListenScope scope) =>
        AtomicFile.WriteAllText(runtimeHome, FileName, scope == ListenScope.OtherDevices ? OtherDevicesText : ThisPcOnlyText);
}
