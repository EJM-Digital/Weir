namespace Weir.Tray;

/// <summary>
/// The progress a server reports on its standard output, which the tray owns: the newest record of one server process. A line is
/// progress the moment the server writes it, whatever is happening to startup-progress.json, so this is the channel a start is
/// judged by; the file is read as well, and the newer of the two counts (<see cref="StartProgressFile.Newer"/>).
/// </summary>
sealed class StartProgressFeed(int processId)
{
    private readonly Lock _gate = new();
    private StartProgress? _latest;

    /// <summary>The newest record the server has reported on its standard output, or null if it has reported none.</summary>
    internal StartProgress? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <summary>
    /// Takes one line the server wrote. Every progress line of this server is kept as the newest record (a beat in the same phase is
    /// progress too). Returns the record when it begins a new phase or step, which is what the log words; null for a beat, and for
    /// any other line, a line that is not whole, or another process's.
    /// </summary>
    internal StartProgress? Accept(string line)
    {
        if (StartProgressFile.ParseLine(line) is not { } record || record.ProcessId != processId)
        {
            return null;
        }

        lock (_gate)
        {
            if (record == _latest)
            {
                return null;
            }

            var phaseChanged = _latest is null || _latest.Phase != record.Phase || _latest.Step != record.Step;
            _latest = record;
            return phaseChanged ? record : null;
        }
    }
}
