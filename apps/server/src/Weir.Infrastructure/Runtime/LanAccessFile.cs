using System.Text;

namespace Weir.Infrastructure.Runtime;

/// <summary>
/// The saved LAN access choice in Weir's data folder: <c>lan-access</c>, holding <c>on</c> (devices on the network)
/// or <c>off</c> (this PC only). The Windows tray owns the file's meaning: it reads it at every start, restarts the
/// server when it changes, and its <c>--allow-lan</c> flag writes it. This class reads and writes the very same text
/// (<c>apps/tray/Weir.Tray/LanAccess/LanAccessSetting.cs</c>), so keep the two in step.
/// </summary>
public sealed class LanAccessFile
{
    public const string FileName = "lan-access";

    private const string NetworkText = "on";
    private const string ThisPcOnlyText = "off";

    private readonly string _home;
    private readonly Action<TimeSpan> _pause;
    private readonly Lock _lastGood = new();
    private (NetworkScope? Scope, bool Known) _last;

    /// <param name="home">Weir's data folder (<c>WEIR_HOME</c>), where the tray looks for the file.</param>
    public LanAccessFile(string home)
        : this(home, Thread.Sleep)
    {
    }

    // The pause is what waits between two looks at a file that is refused; a test replaces it, to see that nothing waits.
    internal LanAccessFile(string home, Action<TimeSpan> pause)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        _home = home;
        _pause = pause;
    }

    /// <summary>
    /// The saved choice, or null when none has been saved. Text the tray would not understand reads as this PC only,
    /// as it does there: opening Weir to the network is never a guess. The tray replaces the file by renaming a new one over it, so a
    /// read never waits for it (<see cref="ReplacedFile"/>): a file that is refused for a moment is the last choice read, and the next
    /// call looks again. Before any choice was read there is nothing to stand in, and this PC only is the safe side.
    /// </summary>
    public NetworkScope? Read()
    {
        var reading = ReplacedFile.ReadText(Path.Join(_home, FileName), attempts: 1, pause: _pause);
        if (reading.State == ReplacedFileState.Busy)
        {
            lock (_lastGood)
            {
                return _last.Known ? _last.Scope : NetworkScope.ThisPcOnly;
            }
        }

        NetworkScope? choice = reading.State == ReplacedFileState.Absent
            ? null
            : reading.Text.Trim() == NetworkText ? NetworkScope.Network : NetworkScope.ThisPcOnly;
        lock (_lastGood)
        {
            _last = (choice, true);
        }

        return choice;
    }

    /// <summary>Saves <paramref name="scope"/>. Writing the same choice again still touches the file, which the tray takes as a request to ask for the firewall rule again.</summary>
    public void Write(NetworkScope scope)
    {
        AtomicFileWriter.Replace(_home, FileName, Encoding.UTF8.GetBytes(scope == NetworkScope.Network ? NetworkText : ThisPcOnlyText));
        lock (_lastGood)
        {
            _last = (scope, true);
        }
    }
}
